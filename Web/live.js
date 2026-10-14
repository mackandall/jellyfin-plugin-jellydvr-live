/*
 * JellyDVR Live — jellyfin-web player buttons for the live TV buffer.
 *
 * While a live channel plays, two buttons sit next to the player's Settings button:
 *   - "Buffer" (history icon): opens the buffer recording at the moment you're watching, so the
 *     normal recording controls can rewind past tune-in, skip commercials, or carry on later
 *     from another device. Pausing itself stays on the live stream.
 *   - "Keep" (bookmark icon): keeps the programme on now as a real recording.
 *   - "Stop" (stop icon): stops watching and stops the channel's buffer at once, so its tuner is
 *     free straight away. Leaving with Back keeps the buffer for the "stop after leaving" time.
 * While a buffer recording plays, "Keep" shows, and "Stop" while that buffer is still recording. In the guide, the recording mark of a
 * programme that is only being buffered is drawn faint, so a solid red dot always means a real
 * recording.
 *
 * Loaded from index.html (install-web.sh). Finds what's playing from this browser's own
 * session, and opens the buffer by sending that session a PlayNow command at a position,
 * which jellyfin-web obeys like any remote-control request.
 */
(function () {
  'use strict';

  var TICKS_PER_MS = 10000;
  var state = { key: null, liveStartMs: null, channelId: null, buffers: [], fetchedMs: 0, itemBuffer: undefined, itemId: null };
  var sessionCache = { at: 0, session: null };

  function api() { return window.ApiClient; }

  function log(msg) { console.debug('[JellyDvrLive] ' + msg); }

  function ownSession() {
    var client = api();
    if (!client) { return Promise.resolve(null); }
    if (Date.now() - sessionCache.at < 4000) { return Promise.resolve(sessionCache.session); }
    return client.getSessions({ deviceId: client.deviceId() }).then(function (sessions) {
      var mine = (sessions || []).filter(function (s) { return s.DeviceId === client.deviceId(); })[0] || null;
      sessionCache = { at: Date.now(), session: mine };
      return mine;
    }, function () { return null; });
  }

  function getJson(path) {
    var client = api();
    return client.ajax({ type: 'GET', url: client.getUrl(path), dataType: 'json' });
  }

  function post(path, method) {
    var client = api();
    return client.ajax({ type: method || 'POST', url: client.getUrl(path), dataType: 'json' });
  }

  // ---- buttons -------------------------------------------------------------------------------

  // The player bar on screen. jellyfin-web can leave an earlier player page in the document,
  // hidden, after playback stops; buttons left in it must not count as "already there".
  function osd() {
    var bars = document.querySelectorAll('.videoOsdBottom');
    for (var i = bars.length - 1; i >= 0; i--) {
      var page = bars[i].closest('#videoOsdPage');
      if (!page || !page.classList.contains('hide')) { return bars[i]; }
    }
    return null;
  }

  function find(cls) {
    var bar = osd();
    return bar ? bar.querySelector('.' + cls) : null;
  }

  // before: the class of the player button the new one goes in front of.
  function button(cls, icon, title, before) {
    var b = find(cls);
    if (b) { return b; }
    var settings = find(before || 'btnVideoOsdSettings');
    if (!settings) { return null; }
    b = document.createElement('button');
    b.type = 'button';
    b.setAttribute('is', 'paper-icon-button-light');
    b.className = 'paper-icon-button-light autoSize ' + cls;
    b.innerHTML = '<span class="largePaperIconButton material-icons ' + icon + '" aria-hidden="true"></span>';
    b.title = title;
    settings.parentNode.insertBefore(b, settings);
    return b;
  }

  function removeButtons() {
    document.querySelectorAll('.jdvrLiveBuffer, .jdvrLiveKeep, .jdvrLiveStop').forEach(function (b) { b.remove(); });
  }

  function setKeepButton(buffer) {
    if (!buffer || !buffer.Owned) {
      var old = find('jdvrLiveKeep');
      if (old) { old.remove(); }
      return;
    }
    var b = button('jdvrLiveKeep', buffer.Kept ? 'bookmark_added' : 'bookmark_add', '');
    if (!b) { return; }
    b.title = buffer.Kept ? 'Kept as a recording (click to undo)' : 'Keep "' + buffer.ProgramName + '" as a recording';
    b.querySelector('.material-icons').className = 'largePaperIconButton material-icons ' + (buffer.Kept ? 'bookmark_added' : 'bookmark_add');
    b.onclick = function () {
      post('JellyDvrLive/Keep/' + buffer.Id, buffer.Kept ? 'DELETE' : 'POST').then(function (updated) {
        Object.assign(buffer, updated);
        setKeepButton(buffer);
      });
    };
  }

  // Stop: the server drops this browser as a viewer and stops the channel's buffer (not a kept
  // one, a real recording, or one someone else is watching); then this browser stops playing,
  // like a remote-control Stop, which closes the live stream.
  function setStopButton(session, channelId) {
    if (!channelId) {
      var old = find('jdvrLiveStop');
      if (old) { old.remove(); }
      return;
    }
    // With the transport buttons, right after Play/Pause.
    var b = button('jdvrLiveStop', 'stop', 'Stop watching and free the tuner now', 'btnFastForward');
    if (!b) { return; }
    b.onclick = function () {
      var stopPlaying = function () { post('Sessions/' + session.Id + '/Playing/Stop'); };
      log('stopping channel ' + channelId);
      post('JellyDvrLive/Channel/' + channelId + '/Stop').then(stopPlaying, stopPlaying);
    };
  }

  // ---- live channel ------------------------------------------------------------------------

  // Wall-clock time of the frame on screen: when this live playback's position 0 aired, plus
  // how far into it the player is (pausing makes this fall behind "now").
  function wallTimeMs(video) {
    return state.liveStartMs + video.currentTime * 1000;
  }

  // When a buffer's recording ends: when it stopped, or (still recording) not yet.
  function bufferEndMs(b) {
    if (b.EndedUtc) { return Date.parse(b.EndedUtc); }
    return b.Recording ? Infinity : Date.parse(b.ProgramEndUtc);
  }

  // The buffer that recorded the moment ms — never an older, finished buffer of this channel
  // (e.g. from an earlier visit, before this visit's buffer has started).
  function bufferAt(ms) {
    var covering = state.buffers.filter(function (b) {
      return b.ItemId && Date.parse(b.StartedUtc) <= ms + 1000 && ms < bufferEndMs(b);
    });
    return covering.length ? covering[covering.length - 1] : null;
  }

  function currentBuffer() {
    var now = Date.now();
    var onNow = state.buffers.filter(function (b) { return Date.parse(b.StartedUtc) <= now && now < bufferEndMs(b); });
    return onNow.length ? onNow[onNow.length - 1] : null;
  }

  // jellyfin-web ignores the start position for a recording still in progress, so after opening a
  // buffer the script seeks there itself once it plays.
  var pendingSeek = null;
  function normId(id) { return String(id || '').replace(/-/g, '').toLowerCase(); }

  function openBuffer(session, video) {
    var ms = wallTimeMs(video);
    var b = bufferAt(ms);
    if (!b) { return; }
    var positionMs = Math.max(0, ms - Date.parse(b.StartedUtc));
    log('opening buffer ' + b.ItemId + ' at ' + Math.round(positionMs / 1000) + ' s');
    pendingSeek = { itemId: normId(b.ItemId), seconds: positionMs / 1000, until: Date.now() + 60000 };
    post('Sessions/' + session.Id + '/Playing?playCommand=PlayNow&itemIds=' + b.ItemId +
      '&startPositionTicks=' + Math.round(positionMs * TICKS_PER_MS));
  }

  function updateLive(session, video) {
    var channelId = session.NowPlayingItem.Id;
    var key = channelId + '|' + (session.PlayState && session.PlayState.PlaySessionId || '');
    if (state.key !== key) {
      state = { key: key, liveStartMs: Date.now() - video.currentTime * 1000, channelId: channelId, buffers: [], fetchedMs: 0 };
    }
    if (Date.now() - state.fetchedMs > 10000) {
      state.fetchedMs = Date.now();
      getJson('JellyDvrLive/Channel/' + channelId).then(function (res) {
        if (state.channelId === channelId) { state.buffers = (res && res.Buffers) || []; }
      }, function () {});
    }

    var b = bufferAt(wallTimeMs(video));
    var replay = b ? button('jdvrLiveBuffer', 'history', 'Open the buffer (rewind past tune-in, skip commercials)') : null;
    if (replay) {
      replay.onclick = function () { openBuffer(session, video); };
    } else {
      var old = find('jdvrLiveBuffer');
      if (old) { old.remove(); }
    }
    setKeepButton(currentBuffer());
    setStopButton(session, channelId);
  }

  // ---- a recording (maybe a buffer) --------------------------------------------------------

  function updateItem(itemId, video) {
    var old = find('jdvrLiveBuffer');
    if (old) { old.remove(); }
    if (pendingSeek && Date.now() > pendingSeek.until) { pendingSeek = null; }
    if (pendingSeek && pendingSeek.itemId === normId(itemId) && video.readyState >= 1) {
      var target = Math.min(pendingSeek.seconds, isFinite(video.duration) ? Math.max(0, video.duration - 5) : pendingSeek.seconds);
      if (Math.abs(video.currentTime - target) > 3) {
        log('seeking buffer to ' + Math.round(target) + ' s');
        video.currentTime = target;
      }
      pendingSeek = null;
    }
    if (state.itemId !== itemId) {
      state = { key: null, itemId: itemId, itemBuffer: undefined };
      getJson('JellyDvrLive/Item/' + itemId).then(function (b) {
        if (state.itemId === itemId) { state.itemBuffer = b; setKeepButton(b); }
      }, function () {
        if (state.itemId === itemId) { state.itemBuffer = null; }
      });
      return;
    }
    if (state.itemBuffer) { setKeepButton(state.itemBuffer); }
    var live = state.itemBuffer && state.itemBuffer.Recording && state.itemBuffer.Owned && !state.itemBuffer.Kept;
    ownSession().then(function (session) {
      if (session && state.itemId === itemId) { setStopButton(session, live ? state.itemBuffer.ChannelId : null); }
    });
  }

  // ---- guide: fainter recording mark for programmes that are only a buffer ----------------

  var bufferIds = new Set();
  var bufferFetchedMs = 0;

  function applyGuideMarks() {
    document.querySelectorAll('.programCell[data-id]').forEach(function (cell) {
      var id = cell.getAttribute('data-id').replace(/-/g, '').toLowerCase();
      cell.querySelectorAll('.timerIcon').forEach(function (icon) {
        icon.classList.toggle('jdvrBufferIcon', bufferIds.has(id));
      });
    });
  }

  function styleGuide() {
    if (!api() || !document.querySelector('.programCell .timerIcon')) { return; }
    if (!document.getElementById('jdvrLiveStyle')) {
      var style = document.createElement('style');
      style.id = 'jdvrLiveStyle';
      style.textContent = '.jdvrBufferIcon { opacity: .35; }';
      document.head.appendChild(style);
    }
    if (Date.now() - bufferFetchedMs > 15000) {
      bufferFetchedMs = Date.now();
      getJson('JellyDvrLive/Programs').then(function (res) {
        bufferIds = new Set(((res && res.ProgramIds) || []).map(function (x) { return x.toLowerCase(); }));
        applyGuideMarks();
      }, function () {});
    }
    applyGuideMarks();
  }

  function tick() {
    try { styleGuide(); } catch (e) { log('guide marks failed: ' + e); }
    var video = document.querySelector('video');
    if (!video || !osd()) {
      if (state.key || state.itemId) { state = { key: null }; }
      return;
    }
    ownSession().then(function (session) {
      var item = session && session.NowPlayingItem;
      if (!item) { removeButtons(); return; }
      if (item.Type === 'TvChannel') {
        updateLive(session, video);
      } else {
        updateItem(item.Id, video);
      }
    });
  }

  setInterval(function () {
    try { tick(); } catch (e) { log('tick failed: ' + e); }
  }, 2000);
})();
