// Compatibility bridge between native C# WebView2 host and Wallpaper Engine web API
(function () {
  if (window.__slwBridgeInstalled) return;
  window.__slwBridgeInstalled = true;

  var audioListeners = [];
  var mediaPropListeners = [];
  var mediaTimelineListeners = [];
  var mediaPlaybackListeners = [];
  var mediaThumbListeners = [];

  window.wallpaperMediaIntegration = {
    PLAYBACK_STOPPED: 0,
    PLAYBACK_PLAYING: 1,
    PLAYBACK_PAUSED: 2
  };

  window.wallpaperRegisterAudioListener = function (cb) {
    if (typeof cb === 'function') audioListeners.push(cb);
  };
  window.wallpaperRegisterMediaPropertiesListener = function (cb) {
    if (typeof cb === 'function') mediaPropListeners.push(cb);
  };
  window.wallpaperRegisterMediaTimelineListener = function (cb) {
    if (typeof cb === 'function') mediaTimelineListeners.push(cb);
  };
  window.wallpaperRegisterMediaPlaybackListener = function (cb) {
    if (typeof cb === 'function') mediaPlaybackListeners.push(cb);
  };
  window.wallpaperRegisterMediaThumbnailListener = function (cb) {
    if (typeof cb === 'function') mediaThumbListeners.push(cb);
  };

  // Helper used by index.html to load arbitrary local files safely via native WebResourceRequested
  window.slwLocalFileUrl = function (rawPath) {
    if (!rawPath) return '';
    if (/^(https?:|data:|blob:)/i.test(rawPath)) return rawPath;
    return 'https://wallpaper.local/localfile?path=' + encodeURIComponent(rawPath);
  };

  // Send media transport control commands (prev / playpause / next) to native SMTC host
  window.slwMediaControl = function (action) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ type: 'mediaControl', action: String(action || '') });
    }
  };
  window.slwMediaSeek = function (positionSec) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ type: 'mediaSeek', position: Number(positionSec) || 0 });
    }
  };

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (ev) {
      var msg = ev.data;
      if (!msg || !msg.type) return;

      switch (msg.type) {
        case 'audio':
          for (var i = 0; i < audioListeners.length; i++) {
            try { audioListeners[i](msg.data); } catch (e) {}
          }
          break;

        case 'mediaProperties':
          for (var i = 0; i < mediaPropListeners.length; i++) {
            try { mediaPropListeners[i](msg.data); } catch (e) {}
          }
          break;

        case 'mediaTimeline':
          for (var i = 0; i < mediaTimelineListeners.length; i++) {
            try { mediaTimelineListeners[i](msg.data); } catch (e) {}
          }
          break;

        case 'mediaPlayback':
          for (var i = 0; i < mediaPlaybackListeners.length; i++) {
            try { mediaPlaybackListeners[i](msg.data); } catch (e) {}
          }
          break;

        case 'mediaThumbnail':
          for (var i = 0; i < mediaThumbListeners.length; i++) {
            try { mediaThumbListeners[i](msg.data); } catch (e) {}
          }
          break;

        case 'properties':
          if (window.wallpaperPropertyListener && typeof window.wallpaperPropertyListener.applyUserProperties === 'function') {
            try { window.wallpaperPropertyListener.applyUserProperties(msg.data); } catch (e) {}
          }
          break;

        case 'paused':
          document.documentElement.classList.toggle('slw-paused', !!msg.value);
          break;

        case 'desktopClick':
          if (typeof msg.nx === 'number' && typeof msg.ny === 'number') {
            var cx = msg.nx * window.innerWidth;
            var cy = msg.ny * window.innerHeight;
            var el = document.elementFromPoint(cx, cy);
            var btn = el && el.closest ? el.closest('.mbtn') : null;
            if (btn) { btn.click(); break; }
            var pbar = document.getElementById('pbar');
            if (pbar && pbar.offsetParent !== null) {
              var r = pbar.getBoundingClientRect();
              if (cx >= r.left - 8 && cx <= r.right + 8 && cy >= r.top - 14 && cy <= r.bottom + 14) {
                var ratio = Math.max(0, Math.min(1, (cx - r.left) / Math.max(1, r.width)));
                if (typeof window.__slwHandleSeekRatio === 'function') window.__slwHandleSeekRatio(ratio);
              }
            }
          }
          break;
      }
    });
  }
})();
