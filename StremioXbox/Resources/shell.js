// Injected into web.stremio.com by the Stremio-XBOX shell before any page script runs.
// 1. Forwards console errors and uncaught errors to the native debug log.
// 2. Watches for Stremio's player route (#/player/<stream>/...), decodes the stream
//    and hands its HTTP(S) URL to the native Xbox player.
(function () {
    'use strict';

    if (window.__stremioXboxShell) {
        return;
    }
    window.__stremioXboxShell = true;

    var host = window.chrome && window.chrome.webview;
    var nativePlayerEnabled = true;

    // Stremio Web treats any page with window.chrome.webview as running inside Stremio's own
    // desktop shell (stremio-web/src/common/Platform/shell/useShell.ts: active = !!chrome.webview)
    // and then plays through the shell's mpv over that bridge. This app is not that shell, so the
    // web player failed with "Stream is not supported". Keep our reference and hide the bridge.
    var bridgeHidden = false;
    if (host) {
        try {
            Object.defineProperty(window.chrome, 'webview', { configurable: true, get: function () { return undefined; } });
            bridgeHidden = window.chrome.webview === undefined;
        } catch (e) {
            bridgeHidden = false;
        }
        if (!bridgeHidden) {
            try {
                var replacement = {};
                Object.getOwnPropertyNames(window.chrome).forEach(function (key) {
                    if (key !== 'webview') {
                        replacement[key] = window.chrome[key];
                    }
                });
                Object.defineProperty(window, 'chrome', { configurable: true, writable: true, value: replacement });
                bridgeHidden = !(window.chrome && window.chrome.webview);
            } catch (e) {
                bridgeHidden = false;
            }
        }
    }
    var lastHandledStream = null;

    function post(message) {
        try {
            if (host) {
                host.postMessage(JSON.stringify(message));
            }
        } catch (e) {
            // Nothing useful to do if the bridge itself fails.
        }
    }

    function format(value) {
        if (value instanceof Error) {
            return value.stack || value.message;
        }
        if (value !== null && typeof value === 'object') {
            try {
                return JSON.stringify(value);
            } catch (e) {
                return String(value);
            }
        }
        return String(value);
    }

    function log(level, text) {
        post({ type: 'log', level: level, text: String(text).slice(0, 2000) });
    }

    ['error', 'warn'].forEach(function (level) {
        var original = console[level];
        console[level] = function () {
            try {
                log(level, Array.prototype.map.call(arguments, format).join(' '));
            } catch (e) {
                // Never break the page's own logging.
            }
            return original.apply(this, arguments);
        };
    });

    window.addEventListener('error', function (event) {
        log('error', 'Uncaught: ' + (event.message || '') + ' @ ' + (event.filename || '') + ':' + (event.lineno || ''));
    });

    window.addEventListener('unhandledrejection', function (event) {
        log('error', 'Unhandled rejection: ' + format(event.reason));
    });

    if (host) {
        host.addEventListener('message', function (event) {
            try {
                var message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
                if (message && message.type === 'config') {
                    nativePlayerEnabled = message.nativePlayer !== false;
                    log('info', 'Native player ' + (nativePlayerEnabled ? 'enabled' : 'disabled'));
                } else if (message && message.type === 'nav') {
                    onNavigate(message.action);
                } else if (message && message.type === 'refocusField') {
                    refocusField(message.id);
                }
            } catch (e) {
                log('error', 'Bad config message: ' + format(e));
            }
        });
    }

    // ---- Controller navigation ----------------------------------------------------------
    // The app reads the controller natively and sends {type:'nav', action}. This moves a
    // highlight between focusable elements by screen position (spatial navigation).
    var FOCUSABLE = 'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])';
    var FOCUS_CLASS = '__sx-focus';
    var nativeNavigation = false;
    var lastKeyLog = 0;

    function isTyping(element) {
        return !!element && (element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.isContentEditable);
    }

    function isCandidate(element) {
        if (element.disabled || element.getAttribute('aria-hidden') === 'true') {
            return false;
        }
        if (typeof element.checkVisibility === 'function' &&
            !element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) {
            return false;
        }
        var rect = element.getBoundingClientRect();
        return rect.width >= 4 && rect.height >= 4;
    }

    // The highlighted element. Text fields are highlighted without focusing them, because focusing a field
    // makes WebView2 open the Xbox keyboard; it opens on A instead.
    var highlighted = null;

    function current() {
        if (highlighted && highlighted.isConnected && isCandidate(highlighted)) {
            return highlighted;
        }
        var active = document.activeElement;
        return active && active !== document.body && active !== document.documentElement && isCandidate(active) ? active : null;
    }

    // Lower is better: distance along the direction plus a penalty for sideways offset.
    function score(from, to, direction) {
        var fx = from.left + from.width / 2;
        var fy = from.top + from.height / 2;
        var tx = to.left + to.width / 2;
        var ty = to.top + to.height / 2;
        var primary;
        var secondary;
        switch (direction) {
            case 'up':
                if (to.bottom > from.top + 1) { return Infinity; }
                primary = from.top - to.bottom;
                secondary = Math.max(0, Math.max(from.left, to.left) < Math.min(from.right, to.right) ? 0 : Math.abs(tx - fx));
                break;
            case 'down':
                if (to.top < from.bottom - 1) { return Infinity; }
                primary = to.top - from.bottom;
                secondary = Math.max(0, Math.max(from.left, to.left) < Math.min(from.right, to.right) ? 0 : Math.abs(tx - fx));
                break;
            case 'left':
                if (to.right > from.left + 1) { return Infinity; }
                primary = from.left - to.right;
                secondary = Math.abs(ty - fy);
                break;
            default:
                if (to.left < from.right - 1) { return Infinity; }
                primary = to.left - from.right;
                secondary = Math.abs(ty - fy);
                break;
        }
        return primary + secondary * 2;
    }

    // Last highlighted element per route, so going back returns to the same tile instead of the top.
    var focusByRoute = {};

    function describeForRestore(element) {
        return {
            href: element.getAttribute('href') || '',
            text: (element.textContent || element.getAttribute('aria-label') || '').trim().slice(0, 80)
        };
    }

    function restoreFocus(route) {
        var saved = focusByRoute[route];
        if (!saved || !nativeNavigation) {
            return;
        }
        var tries = 0;
        (function attempt() {
            if (location.hash !== route) {
                return;
            }
            var found = null;
            if (saved.href) {
                found = Array.prototype.filter.call(document.querySelectorAll('a[href]'), function (el) {
                    return el.getAttribute('href') === saved.href && isCandidate(el);
                })[0] || null;
            }
            if (!found && saved.text) {
                found = Array.prototype.filter.call(document.querySelectorAll(FOCUSABLE), function (el) {
                    return isCandidate(el) && (el.textContent || el.getAttribute('aria-label') || '').trim().slice(0, 80) === saved.text;
                })[0] || null;
            }
            if (found) {
                setFocus(found, 'center');
            } else if (++tries < 20) {
                setTimeout(attempt, 150); // Stremio renders the page a moment after the route changes.
            }
        })();
    }

    function setFocus(element, block) {
        focusByRoute[location.hash] = describeForRestore(element);
        var previous = document.querySelector('.' + FOCUS_CLASS);
        if (previous) {
            previous.classList.remove(FOCUS_CLASS);
        }
        element.classList.add(FOCUS_CLASS);
        highlighted = element;
        if (isTyping(element)) {
            if (document.activeElement && document.activeElement !== document.body) {
                document.activeElement.blur();
            }
        } else {
            element.focus({ preventScroll: true });
        }
        element.scrollIntoView({ block: block || 'nearest', inline: 'nearest', behavior: block ? 'auto' : 'smooth' });
        // After the scroll settles, re-centre if the item is off screen or covered (e.g. by a sticky header):
        // on Settings the Log in / Sign up link ended up hidden while highlighted.
        setTimeout(function () { ensureVisible(element); }, 400);
    }

    function describeElement(element) {
        if (!element) {
            return 'none';
        }
        var r = element.getBoundingClientRect();
        return element.tagName.toLowerCase() + (element.className && typeof element.className === 'string' ? '.' + element.className.split(' ')[0] : '') +
            ' "' + (element.textContent || '').trim().slice(0, 30) + '" at ' + Math.round(r.left) + ',' + Math.round(r.top) + ' ' + Math.round(r.width) + 'x' + Math.round(r.height);
    }

    function ensureVisible(element) {
        if (highlighted !== element || !element.isConnected) {
            return;
        }
        var r = element.getBoundingClientRect();
        var hit = document.elementFromPoint(r.left + r.width / 2, r.top + Math.min(r.height / 2, 20));
        var covered = !hit || !(hit === element || element.contains(hit) || hit.contains(element));
        var offScreen = r.top < 0 || r.bottom > innerHeight || r.left < 0 || r.right > innerWidth;
        if (/^#\/settings/.test(location.hash)) {
            log('info', 'Settings highlight: ' + describeElement(element) + (covered ? ', covered by ' + describeElement(hit) : '') + (offScreen ? ', off screen' : ''));
        }
        if (covered || offScreen) {
            element.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'auto' });
        }
    }

    function move(direction) {
        var candidates = Array.prototype.filter.call(document.querySelectorAll(FOCUSABLE), isCandidate);
        if (candidates.length === 0) {
            return;
        }
        var from = current();
        if (!from) {
            // Nothing focused yet: start from the top-left-most visible element.
            var visible = candidates.filter(function (el) {
                var r = el.getBoundingClientRect();
                return r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
            });
            visible.sort(function (a, b) {
                var ra = a.getBoundingClientRect();
                var rb = b.getBoundingClientRect();
                return (ra.top - rb.top) || (ra.left - rb.left);
            });
            setFocus(visible[0] || candidates[0]);
            return;
        }
        var fromRect = from.getBoundingClientRect();
        var best = null;
        var bestScore = Infinity;
        candidates.forEach(function (el) {
            if (el === from || from.contains(el) || el.contains(from)) {
                return;
            }
            var s = score(fromRect, el.getBoundingClientRect(), direction);
            if (s < bestScore) {
                bestScore = s;
                best = el;
            }
        });
        if (best) {
            setFocus(best);
        }
    }

    // Stremio's web player: Space (keydown + keyup) toggles play/pause, arrows seek.
    function pressKey(key, code, keyCode) {
        var target = document.activeElement || document.body;
        ['keydown', 'keyup'].forEach(function (type) {
            target.dispatchEvent(new KeyboardEvent(type, {
                key: key, code: code, keyCode: keyCode, which: keyCode, bubbles: true, cancelable: true,
            }));
        });
    }

    // Typing: when a Stremio text field gets focus (A on the highlight, a cursor click, or Stremio focusing
    // it itself, e.g. the login page), the app passes focus through a native text box and back, then asks
    // for the field to be focused again; WebView2's own Xbox keyboard then types into it.
    var textFields = {};
    var nextTextField = 0;
    var ignoreFocusUntil = 0;

    function registerField(element) {
        if (!element.__sxField) {
            element.__sxField = ++nextTextField;
        }
        textFields[element.__sxField] = element;
    }

    // Fields focused some other way (Stremio's own autofocus, cursor clicks): the app does its pass only if
    // typing has not been set up yet since the page loaded.
    document.addEventListener('focusin', function (event) {
        var element = event.target;
        if (!isTyping(element) || Date.now() < ignoreFocusUntil) {
            return;
        }
        registerField(element);
        post({ type: 'textFocus', id: element.__sxField });
    }, true);

    function refocusField(id) {
        var element = textFields[id];
        if (!element || !element.isConnected) {
            return;
        }
        ignoreFocusUntil = Date.now() + 800;
        element.focus();
    }

    function onNavigate(action) {
        nativeNavigation = true;
        if (/^#\/player\//.test(location.hash)) {
            if (action === 'activate') {
                pressKey(' ', 'Space', 32);
                return;
            }
            if (action === 'left') {
                pressKey('ArrowLeft', 'ArrowLeft', 37);
                return;
            }
            if (action === 'right') {
                pressKey('ArrowRight', 'ArrowRight', 39);
                return;
            }
        }
        var from = current();
        if (action === 'activate') {
            if (from) {
                if (isTyping(from)) {
                    // Ask the app first, so its focus pass happens before the field (and the keyboard) opens;
                    // the app answers with refocusField.
                    registerField(from);
                    post({ type: 'typingStart', id: from.__sxField });
                    return;
                }
                if (/^#\/settings/.test(location.hash)) {
                    log('info', 'Settings activate: ' + describeElement(from));
                }
                from.click();
            } else {
                move('down');
            }
            return;
        }
        if (isTyping(from) && (action === 'left' || action === 'right')) {
            return; // Let the caret move inside text fields.
        }
        move(action);
    }

    // Raw keys from WebView2 itself: logged (to learn what the Xbox sends) and, once native
    // navigation is active, swallowed so a D-pad press is not handled twice.
    document.addEventListener('keydown', function (event) {
        if (!event.isTrusted) {
            return;
        }
        if (Date.now() - lastKeyLog > 2000) {
            lastKeyLog = Date.now();
            log('info', 'Key from WebView2: key=' + event.key + ' code=' + event.code + ' keyCode=' + event.keyCode);
        }
        // Controller B (WebView2 sends keyCode 27 with key "Unidentified", or GamepadB) when the page has
        // focus. Off the home screen it goes back in Stremio's own history; on home it is left as it was.
        if (((event.keyCode === 27 && event.key !== 'Escape') || event.key === 'GamepadB') && !isHome() &&
            !isTyping(document.activeElement)) {
            event.preventDefault();
            event.stopImmediatePropagation();
            window.__sxBack();
            return;
        }
        if (!nativeNavigation) {
            return;
        }
        var typing = isTyping(event.target);
        var isArrow = /^Arrow/.test(event.key);
        if ((isArrow && !(typing && (event.key === 'ArrowLeft' || event.key === 'ArrowRight'))) ||
            (event.key === 'Enter' && !typing) ||
            /^Gamepad/.test(event.key)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);

    // Mouse use clears the controller highlight.
    document.addEventListener('mousedown', function () {
        var highlighted = document.querySelector('.' + FOCUS_CLASS);
        if (highlighted) {
            highlighted.classList.remove(FOCUS_CLASS);
        }
    }, true);

    function addFocusStyle() {
        var style = document.createElement('style');
        style.textContent = '.' + FOCUS_CLASS + '{outline:4px solid #7B5BF5 !important;outline-offset:3px !important;' +
            'box-shadow:0 0 0 8px rgba(123,91,245,.35) !important;border-radius:8px;}';
        (document.head || document.documentElement).appendChild(style);
    }

    // stremio-core encodes the stream as: JSON -> zlib -> standard base64 -> percent-encoding.
    function decodeStream(encoded) {
        var binary = atob(decodeURIComponent(encoded));
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        var inflated = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate'));
        return new Response(inflated).text().then(JSON.parse);
    }

    function stopWebPlayer() {
        Array.prototype.forEach.call(document.querySelectorAll('video'), function (video) {
            try {
                video.pause();
                video.removeAttribute('src');
                video.load();
            } catch (e) {
                // Ignore; the element may already be gone.
            }
        });
    }

    // ---- Subtitles from the user's addons --------------------------------------------------
    var SUBTITLE_TIMEOUT_MS = 4000;
    var MAX_SUBTITLES = 20;          // the native player downloads each one when the file opens
    var MAX_PER_LANGUAGE = 3;

    function readProfile() {
        try {
            return JSON.parse(localStorage.getItem('profile'));
        } catch (e) {
            return null;
        }
    }

    function providesSubtitles(manifest, type, id) {
        var resources = (manifest && manifest.resources) || [];
        return resources.some(function (resource) {
            var name = typeof resource === 'string' ? resource : resource.name;
            if (name !== 'subtitles') {
                return false;
            }
            var types = (typeof resource === 'object' && resource.types) || manifest.types || [];
            var prefixes = (typeof resource === 'object' && resource.idPrefixes) || manifest.idPrefixes;
            var typeOk = types.length === 0 || types.indexOf(type) !== -1;
            var idOk = !prefixes || prefixes.length === 0 || prefixes.some(function (prefix) { return id.indexOf(prefix) === 0; });
            return typeOk && idOk;
        });
    }

    function fetchWithTimeout(url, ms) {
        var controller = typeof AbortController === 'function' ? new AbortController() : null;
        var timer = setTimeout(function () { if (controller) { controller.abort(); } }, ms);
        return fetch(url, controller ? { signal: controller.signal } : {}).then(function (response) {
            clearTimeout(timer);
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        }, function (error) {
            clearTimeout(timer);
            throw error;
        });
    }

    // Returns [{url, lang, label}], subtitles bundled with the stream first, then addon results,
    // with the user's preferred subtitle language (Stremio setting) sorted to the top.
    function collectSubtitles(stream, type, videoId) {
        var list = (stream.subtitles || []).filter(function (sub) { return sub && sub.url; }).map(function (sub) {
            return { url: sub.url, lang: sub.lang || '', label: (sub.lang || '?') + ' (stream)' };
        });
        var profile = readProfile();
        if (!profile || !type || !videoId) {
            log('warn', 'Addon subtitles skipped: ' + (!profile ? 'no saved Stremio profile' : 'no title id in the player route'));
            return Promise.resolve(list);
        }
        var addons = (profile.addons || []).filter(function (addon) {
            return addon && addon.transportUrl && providesSubtitles(addon.manifest, type, videoId);
        });
        var requests = addons.map(function (addon) {
            var base = addon.transportUrl.replace(/\/manifest\.json$/, '');
            var name = (addon.manifest && addon.manifest.name) || 'addon';
            var url = base + '/subtitles/' + encodeURIComponent(type) + '/' + encodeURIComponent(videoId) + '.json';
            return fetchWithTimeout(url, SUBTITLE_TIMEOUT_MS).then(function (result) {
                return (result.subtitles || []).filter(function (sub) { return sub && sub.url; }).map(function (sub) {
                    return { url: sub.url, lang: sub.lang || '', label: (sub.lang || '?') + ' (' + name + ')' };
                });
            }).catch(function (e) {
                log('warn', 'Subtitles from ' + name + ' failed: ' + format(e));
                return [];
            });
        });
        return Promise.all(requests).then(function (results) {
            results.forEach(function (result) { list = list.concat(result); });
            var preferred = profile.settings && profile.settings.subtitlesLanguage;
            if (preferred) {
                list.sort(function (a, b) { return (b.lang === preferred) - (a.lang === preferred); });
            }
            var perLanguage = {};
            var picked = list.filter(function (sub) {
                perLanguage[sub.lang] = (perLanguage[sub.lang] || 0) + 1;
                return perLanguage[sub.lang] <= MAX_PER_LANGUAGE;
            }).slice(0, MAX_SUBTITLES);
            log('info', 'Subtitles found: ' + list.length + ' from ' + addons.length + ' addon(s)' + (preferred ? ', preferred ' + preferred : '') + '; passing ' + picked.length);
            return picked;
        });
    }

    // Back for both B paths (page key and the app's system back): Stremio routes with history.pushState,
    // which WebView2's GoBack does not follow, so use the page's history. One press may arrive through
    // both paths; act once.
    var lastBack = 0;
    function isHome() {
        return location.hash === '' || location.hash === '#' || location.hash === '#/';
    }
    window.__sxBack = function () {
        if (isHome()) {
            return false;
        }
        if (Date.now() - lastBack > 500) {
            lastBack = Date.now();
            history.back();
        }
        return true;
    };

    function onRouteChanged() {
        post({ type: 'route', home: isHome() });
        restoreFocus(location.hash);
        var match = /^#\/player\/([^/?]+)/.exec(location.hash);
        if (!match) {
            lastHandledStream = null;
            return;
        }
        if (match[1] === lastHandledStream) {
            return;
        }
        lastHandledStream = match[1];

        // #/player/<stream>/<streamTransportUrl>/<metaTransportUrl>/<type>/<id>/<videoId>
        var parts = location.hash.slice('#/player/'.length).split('?')[0].split('/').map(function (part) {
            try { return decodeURIComponent(part); } catch (e) { return part; }
        });
        var type = parts[3] || '';
        var videoId = parts[5] || parts[4] || '';

        decodeStream(match[1]).then(function (stream) {
            log('info', 'Stream: ' + JSON.stringify(stream).slice(0, 1500));

            if (!nativePlayerEnabled) {
                log('info', 'Native player is off; leaving playback to the web player.');
                return;
            }
            if (typeof stream.url !== 'string' || !/^https?:\/\//i.test(stream.url)) {
                log('warn', 'Stream has no http(s) url; leaving it to the web player.');
                return;
            }

            var hints = stream.behaviorHints || {};
            stopWebPlayer();
            // Leave the web player route so it does not keep loading the same file.
            history.back();

            collectSubtitles(stream, type, videoId).then(function (subtitles) {
                post({
                    type: 'play',
                    url: stream.url,
                    name: stream.name || '',
                    description: stream.description || stream.title || '',
                    filename: hints.filename || '',
                    hasRequestHeaders: !!(hints.proxyHeaders && hints.proxyHeaders.request),
                    subtitles: subtitles
                });
            });
        }).catch(function (e) {
            log('error', 'Could not decode player stream: ' + format(e));
        });
    }

    // Stremio Web uses React Router's HashRouter, which navigates with history.pushState;
    // that does not fire 'hashchange', so hook the history API as well.
    ['pushState', 'replaceState'].forEach(function (method) {
        var original = history[method];
        history[method] = function () {
            var result = original.apply(this, arguments);
            setTimeout(onRouteChanged, 0);
            return result;
        };
    });
    window.addEventListener('popstate', onRouteChanged);
    window.addEventListener('hashchange', onRouteChanged);
    window.addEventListener('DOMContentLoaded', function () {
        log('info', 'User agent: ' + navigator.userAgent);
        log(bridgeHidden ? 'info' : 'warn', 'WebView2 bridge hidden from Stremio Web: ' + bridgeHidden);
        addFocusStyle();
        onRouteChanged();
    });
})();
