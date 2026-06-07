using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SSO_Auth;

/// <summary>
/// A helper class to return HTML for the client's auth flow.
/// </summary>
public static class WebResponse
{
    /// <summary>
    /// The shared HTML between all of the responses.
    /// </summary>
    public static readonly string Base = @"<!DOCTYPE html>
<html lang='en'><head>
<meta charset='utf-8'>
<title>Signing in...</title>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<link rel='icon' href=""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='8' cy='8' r='7' fill='%2300a4dc'/%3E%3C/svg%3E"">
<style>
  html, body {
    height: 100%;
    margin: 0;
  }
  body {
    background: #101010;
    color: #d1cfce;
    font-family: Noto Sans, Noto Sans HK, Noto Sans JP, Noto Sans KR, Noto Sans SC, Noto Sans TC, sans-serif;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .sso-card {
    width: 320px;
    max-width: 80vw;
    text-align: center;
    animation: sso-fadein 0.45s ease both;
  }
  .sso-logo {
    display: block;
    width: 72px;
    height: auto;
    margin: 0 auto 28px;
  }
  .sso-spinner {
    width: 44px;
    height: 44px;
    margin: 0 auto 24px;
    border: 4px solid rgba(255, 255, 255, 0.15);
    border-top-color: #00a4dc;
    border-radius: 50%;
    animation: sso-spin 0.9s linear infinite;
  }
  .sso-status {
    font-size: 15px;
    margin: 0 0 20px;
    color: #d1cfce;
    min-height: 20px;
    transition: opacity 0.2s ease;
  }
  .sso-track {
    width: 100%;
    height: 4px;
    background: rgba(255, 255, 255, 0.12);
    border-radius: 2px;
    overflow: hidden;
  }
  .sso-bar {
    width: 40%;
    height: 100%;
    background: #00a4dc;
    border-radius: 2px;
    animation: sso-indeterminate 1.4s ease-in-out infinite;
  }
  .sso-bar.sso-error {
    width: 100%;
    background: #c84a4a;
    animation: none;
  }
  .sso-actions {
    margin-top: 22px;
  }
  .sso-btn {
    display: inline-block;
    padding: 10px 22px;
    font-size: 14px;
    font-family: inherit;
    color: #fff;
    background: #00a4dc;
    border: 0;
    border-radius: 6px;
    cursor: pointer;
    text-decoration: none;
    transition: background 0.15s ease;
  }
  .sso-btn:hover {
    background: #0b8fbd;
  }
  @keyframes sso-spin {
    to { transform: rotate(360deg); }
  }
  @keyframes sso-indeterminate {
    0% { margin-left: -40%; }
    100% { margin-left: 100%; }
  }
  @keyframes sso-fadein {
    from { opacity: 0; transform: translateY(8px); }
    to { opacity: 1; transform: none; }
  }
  @media (prefers-reduced-motion: reduce) {
    .sso-card, .sso-spinner, .sso-bar { animation: none !important; }
    .sso-status { transition: none !important; }
  }
</style>
</head><body>
<div class='sso-card'>
  <svg class='sso-logo' viewBox='0 0 24 24' role='img' aria-label='Secure sign-in' fill='none' xmlns='http://www.w3.org/2000/svg'>
    <path d='M7 10V7a5 5 0 0 1 10 0v3' stroke='#00a4dc' stroke-width='2' stroke-linecap='round'/>
    <rect x='4' y='10' width='16' height='11' rx='2.5' fill='#00a4dc'/>
    <circle cx='12' cy='14.5' r='1.7' fill='#101010'/>
    <rect x='11.3' y='14.8' width='1.4' height='3.6' rx='0.7' fill='#101010'/>
  </svg>
  <div class='sso-spinner' id='sso-spinner'></div>
  <p class='sso-status' id='sso-status' role='status' aria-live='polite'>Logging in...</p>
  <div class='sso-track'><div class='sso-bar' id='sso-bar'></div></div>
  <div class='sso-actions' id='sso-actions'></div>
  <noscript>Please enable Javascript to complete the login</noscript>
</div>
<script>

function isTv() {
    // This is going to be really difficult to get right
    const userAgent = navigator.userAgent.toLowerCase();

    // The OculusBrowsers userAgent also has the samsungbrowser defined but is not a tv.
    if (userAgent.indexOf('oculusbrowser') !== -1) {
        return false;
    }

    if (userAgent.indexOf('tv') !== -1) {
        return true;
    }

    if (userAgent.indexOf('samsungbrowser') !== -1) {
        return true;
    }

    if (userAgent.indexOf('viera') !== -1) {
        return true;
    }

    return isWeb0s();
}

function isWeb0s() {
    const userAgent = navigator.userAgent.toLowerCase();

    return userAgent.indexOf('netcast') !== -1
        || userAgent.indexOf('web0s') !== -1;
}

function isMobile(userAgent) {
    const terms = [
        'mobi',
        'ipad',
        'iphone',
        'ipod',
        'silk',
        'gt-p1000',
        'nexus 7',
        'kindle fire',
        'opera mini'
    ];

    const lower = userAgent.toLowerCase();

    for (let i = 0, length = terms.length; i < length; i++) {
        if (lower.indexOf(terms[i]) !== -1) {
            return true;
        }
    }

    return false;
}

function hasKeyboard(browser) {
    if (browser.touch) {
        return true;
    }

    if (browser.xboxOne) {
        return true;
    }

    if (browser.ps4) {
        return true;
    }

    if (browser.edgeUwp) {
        // This is OK for now, but this won't always be true
        // Should we use this?
        // https://gist.github.com/wagonli/40d8a31bd0d6f0dd7a5d
        return true;
    }

    return !!browser.tv;
}

function iOSversion() {
    // MacIntel: Apple iPad Pro 11 iOS 13.1
    if (/iP(hone|od|ad)|MacIntel/.test(navigator.platform)) {
        const tests = [
            // Original test for getting full iOS version number in iOS 2.0+
            /OS (\d+)_(\d+)_?(\d+)?/,
            // Test for iPads running iOS 13+ that can only get the major OS version
            /Version\/(\d+)/
        ];
        for (const test of tests) {
            const matches = (navigator.appVersion).match(test);
            if (matches) {
                return [
                    parseInt(matches[1], 10),
                    parseInt(matches[2] || 0, 10),
                    parseInt(matches[3] || 0, 10)
                ];
            }
        }
    }
    return [];
}

function web0sVersion(browser) {
    // Detect webOS version by web engine version

    if (browser.chrome) {
        const userAgent = navigator.userAgent.toLowerCase();

        if (userAgent.indexOf('netcast') !== -1) {
            // The built-in browser (NetCast) may have a version that doesn't correspond to the actual web engine
            // Since there is no reliable way to detect webOS version, we return an undefined version

            console.warn('Unable to detect webOS version - NetCast');

            return undefined;
        }

        // The next is only valid for the app

        if (browser.versionMajor >= 94) {
            return 23;
        } else if (browser.versionMajor >= 87) {
            return 22;
        } else if (browser.versionMajor >= 79) {
            return 6;
        } else if (browser.versionMajor >= 68) {
            return 5;
        } else if (browser.versionMajor >= 53) {
            return 4;
        } else if (browser.versionMajor >= 38) {
            return 3;
        } else if (browser.versionMajor >= 34) {
            // webOS 2 browser
            return 2;
        } else if (browser.versionMajor >= 26) {
            // webOS 1 browser
            return 1;
        }
    } else if (browser.versionMajor >= 538) {
        // webOS 2 app
        return 2;
    } else if (browser.versionMajor >= 537) {
        // webOS 1 app
        return 1;
    }

    console.error('Unable to detect webOS version');

    return undefined;
}

let _supportsCssAnimation;
let _supportsCssAnimationWithPrefix;
function supportsCssAnimation(allowPrefix) {
    // TODO: Assess if this is still needed, as all of our targets should natively support CSS animations.
    if (allowPrefix && (_supportsCssAnimationWithPrefix === true || _supportsCssAnimationWithPrefix === false)) {
        return _supportsCssAnimationWithPrefix;
    }
    if (_supportsCssAnimation === true || _supportsCssAnimation === false) {
        return _supportsCssAnimation;
    }

    let animation = false;
    const domPrefixes = ['Webkit', 'O', 'Moz'];
    const elm = document.createElement('div');

    if (elm.style.animationName !== undefined) {
        animation = true;
    }

    if (animation === false && allowPrefix) {
        for (const domPrefix of domPrefixes) {
            if (elm.style[domPrefix + 'AnimationName'] !== undefined) {
                animation = true;
                break;
            }
        }
    }

    if (allowPrefix) {
        _supportsCssAnimationWithPrefix = animation;
        return _supportsCssAnimationWithPrefix;
    } else {
        _supportsCssAnimation = animation;
        return _supportsCssAnimation;
    }
}

const uaMatch = function (ua) {
    ua = ua.toLowerCase();

    const match = /(chrome)[ /]([\w.]+)/.exec(ua)
        || /(edg)[ /]([\w.]+)/.exec(ua)
        || /(edga)[ /]([\w.]+)/.exec(ua)
        || /(edgios)[ /]([\w.]+)/.exec(ua)
        || /(edge)[ /]([\w.]+)/.exec(ua)
        || /(opera)[ /]([\w.]+)/.exec(ua)
        || /(opr)[ /]([\w.]+)/.exec(ua)
        || /(safari)[ /]([\w.]+)/.exec(ua)
        || /(firefox)[ /]([\w.]+)/.exec(ua)
        || ua.indexOf('compatible') < 0 && /(mozilla)(?:.*? rv:([\w.]+)|)/.exec(ua)
        || [];

    const versionMatch = /(version)[ /]([\w.]+)/.exec(ua);

    let platform_match = /(ipad)/.exec(ua)
        || /(iphone)/.exec(ua)
        || /(windows)/.exec(ua)
        || /(android)/.exec(ua)
        || [];

    let browser = match[1] || '';

    if (browser === 'edge') {
        platform_match = [''];
    }

    if (browser === 'opr') {
        browser = 'opera';
    }

    let version;
    if (versionMatch && versionMatch.length > 2) {
        version = versionMatch[2];
    }

    version = version || match[2] || '0';

    let versionMajor = parseInt(version.split('.')[0], 10);

    if (isNaN(versionMajor)) {
        versionMajor = 0;
    }

    return {
        browser: browser,
        version: version,
        platform: platform_match[0] || '',
        versionMajor: versionMajor
    };
};

const userAgent = navigator.userAgent;

const matched = uaMatch(userAgent);
const browser = {};

if (matched.browser) {
    browser[matched.browser] = true;
    browser.version = matched.version;
    browser.versionMajor = matched.versionMajor;
}

if (matched.platform) {
    browser[matched.platform] = true;
}

browser.edgeChromium = browser.edg || browser.edga || browser.edgios;

if (!browser.chrome && !browser.edgeChromium && !browser.edge && !browser.opera && userAgent.toLowerCase().indexOf('webkit') !== -1) {
    browser.safari = true;
}

browser.osx = userAgent.toLowerCase().indexOf('mac os x') !== -1;

// This is a workaround to detect iPads on iOS 13+ that report as desktop Safari
// This may break in the future if Apple releases a touchscreen Mac
// https://forums.developer.apple.com/thread/119186
if (browser.osx && !browser.iphone && !browser.ipod && !browser.ipad && navigator.maxTouchPoints > 1) {
    browser.ipad = true;
}

if (userAgent.toLowerCase().indexOf('playstation 4') !== -1) {
    browser.ps4 = true;
    browser.tv = true;
}

if (isMobile(userAgent)) {
    browser.mobile = true;
}

if (userAgent.toLowerCase().indexOf('xbox') !== -1) {
    browser.xboxOne = true;
    browser.tv = true;
}
browser.animate = typeof document !== 'undefined' && document.documentElement.animate != null;
browser.hisense = userAgent.toLowerCase().includes('hisense');
browser.tizen = userAgent.toLowerCase().indexOf('tizen') !== -1 || window.tizen != null;
browser.vidaa = userAgent.toLowerCase().includes('vidaa');
browser.web0s = isWeb0s();
browser.edgeUwp = browser.edge && (userAgent.toLowerCase().indexOf('msapphost') !== -1 || userAgent.toLowerCase().indexOf('webview') !== -1);

if (browser.web0s) {
    browser.web0sVersion = web0sVersion(browser);
} else if (browser.tizen) {
    // UserAgent string contains 'Safari' and 'safari' is set by matched browser, but we only want 'tizen' to be true
    delete browser.safari;

    const v = (navigator.appVersion).match(/Tizen (\d+).(\d+)/);
    browser.tizenVersion = parseInt(v[1], 10);
} else {
    browser.orsay = userAgent.toLowerCase().indexOf('smarthub') !== -1;
}

if (browser.edgeUwp) {
    browser.edge = true;
}

browser.tv = isTv();
browser.operaTv = browser.tv && userAgent.toLowerCase().indexOf('opr/') !== -1;

if (browser.mobile || browser.tv) {
    browser.slow = true;
}

/* eslint-disable-next-line compat/compat */
if (typeof document !== 'undefined' && ('ontouchstart' in window) || (navigator.maxTouchPoints > 0)) {
    browser.touch = true;
}

browser.keyboard = hasKeyboard(browser);
browser.supportsCssAnimation = supportsCssAnimation;

browser.iOS = browser.ipad || browser.iphone || browser.ipod;

if (browser.iOS) {
    browser.iOSVersion = iOSversion();

    if (browser.iOSVersion && browser.iOSVersion.length >= 2) {
        browser.iOSVersion = browser.iOSVersion[0] + (browser.iOSVersion[1] / 10);
    }
}

function getDeviceName() {
	var deviceName = '';
    if (!deviceName) {
        if (browser.tizen) {
            deviceName = 'Samsung Smart TV';
        } else if (browser.web0s) {
            deviceName = 'LG Smart TV';
        } else if (browser.operaTv) {
            deviceName = 'Opera TV';
        } else if (browser.xboxOne) {
            deviceName = 'Xbox One';
        } else if (browser.ps4) {
            deviceName = 'Sony PS4';
        } else if (browser.chrome) {
            deviceName = 'Chrome';
        } else if (browser.edgeChromium) {
            deviceName = 'Edge Chromium';
        } else if (browser.edge) {
            deviceName = 'Edge';
        } else if (browser.firefox) {
            deviceName = 'Firefox';
        } else if (browser.opera) {
            deviceName = 'Opera';
        } else if (browser.safari) {
            deviceName = 'Safari';
        } else {
            deviceName = 'Web Browser';
        }

        if (browser.ipad) {
            deviceName += ' iPad';
        } else if (browser.iphone) {
            deviceName += ' iPhone';
        } else if (browser.android) {
            deviceName += ' Android';
        }
    }

    return deviceName;
}

const sleep = (milliseconds) => {
    return new Promise(resolve => setTimeout(resolve, milliseconds))
}

function setStatus(text) {
    const el = document.getElementById('sso-status');
    if (!el) return;
    el.style.opacity = '0';
    setTimeout(function () { el.textContent = text; el.style.opacity = '1'; }, 150);
}

function hideProgress() {
    const spinner = document.getElementById('sso-spinner');
    if (spinner) spinner.style.display = 'none';
}

function addButton(label, href) {
    const actions = document.getElementById('sso-actions');
    if (!actions || actions.querySelector('a')) return;
    const a = document.createElement('a');
    a.className = 'sso-btn';
    a.textContent = label;
    a.href = href;
    actions.appendChild(a);
}

function setError(text) {
    if (typeof ssoTimeout !== 'undefined') clearTimeout(ssoTimeout);
    setStatus(text);
    hideProgress();
    const bar = document.getElementById('sso-bar');
    if (bar) bar.classList.add('sso-error');
    addButton('Return to login', ssoWebUrl);
}

function setStuck() {
    setStatus('This is taking longer than usual. You can keep waiting, or try again.');
    addButton('Try again', ssoWebUrl);
}

";

    /// <summary>
    /// A generator for the web response that incorporates the data from the server.
    /// </summary>
    /// <param name="data">The data of the auth flow. Is signed XML for SAML and a state ID for OpenID.</param>
    /// <param name="provider">The name of the provider to callback to.</param>
    /// <param name="baseUrl">The base URL of the Jellyfin installation.</param>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="isLinking">Whether or not this request is to link accounts (Rather than authenticate).</param>
    /// <returns>A string with the HTML to serve to the client.</returns>
    public static string Generator(string data, string provider, string baseUrl, string mode, bool isLinking = false)
    {
        // Strip out the protocol (http:// or https://) and convert the domain to Punycode
        var idnMapping = new IdnMapping();
        var protocolSeparatorIndex = baseUrl.IndexOf("//");
        var protocol = baseUrl.Substring(0, protocolSeparatorIndex + 2);
        var domain = baseUrl.Substring(protocolSeparatorIndex + 2);
        var punycodeDomain = idnMapping.GetAscii(domain);
        var punycodeBaseUrl = protocol + punycodeDomain;

        // Escape interpolated values for the JS string context (provider display + auth payload), and
        // URL-encode the provider where it is used as a path segment.
        var providerJs = EscapeJsString(provider);
        var providerPath = Uri.EscapeDataString(provider ?? string.Empty);
        var dataJs = EscapeJsString(data);

        return Base + @"
var ssoBaseUrl = '" + punycodeBaseUrl + @"';
var ssoWebUrl = ssoBaseUrl + '/web/index.html';
var ssoProviderName = '" + providerJs + @"';
var ssoProviderDisplay = ssoProviderName ? ssoProviderName.charAt(0).toUpperCase() + ssoProviderName.slice(1) : '';
var ssoTimeout;

async function link(request) {
    const jfCredentialsString = localStorage.getItem(""jellyfin_credentials"");

    if (jfCredentialsString == null) return;

    const jfCredentials = JSON.parse(jfCredentialsString);
    const jfUser = jfCredentials['Servers'][0]['UserId'];
    const jfToken = jfCredentials['Servers'][0]['AccessToken'];

    if (jfUser == null) return;
    if (jfToken == null) return;

    const url = '" + $"{punycodeBaseUrl}/sso/{mode}/Link/{providerPath}/" + @"' + jfUser;

    return new Promise(resolve => {
       var xhr = new XMLHttpRequest();
       xhr.open('POST', url, true);
       xhr.setRequestHeader('Content-Type', 'application/json');
       xhr.setRequestHeader('Accept', 'application/json');

       xhr.setRequestHeader(
           'X-Emby-Authorization', 
           `MediaBrowser Client=""${request.appName}"",Device=""${request.deviceName}"",DeviceId=""${request.deviceId}"",Version=""${request.appVersion}"",Token=""${jfToken}""`)

       xhr.onload = function(e) {
         resolve(xhr.response);
       };
       xhr.onerror = function (e) {
         console.log(e);
         resolve(undefined);
       };
       xhr.send(JSON.stringify(request));
    })
}

async function main() {
    localStorage.removeItem('jellyfin_credentials');
    document.getElementById('iframe-main').src = '" + punycodeBaseUrl + @"/web/index.html';

    ssoTimeout = setTimeout(setStuck, 20000);
    setStatus('Connecting to your account...');
    var data = '" + dataJs + @"';
    while (localStorage.getItem(""_deviceId2"") == null ||
        localStorage.getItem(""jellyfin_credentials"") == null ||
        JSON.parse(localStorage.getItem(""jellyfin_credentials""))['Servers'][0]['Id'] == null) {
        // If localStorage isn't initialized yet, try again.
        await sleep(100);
    }
    var deviceId = localStorage.getItem(""_deviceId2"");
    var appName = ""Jellyfin Web"";
    var appVersion = ""10.11.0"";
    try {
        var infoResp = await fetch(ssoBaseUrl + '/System/Info/Public');
        if (infoResp.ok) {
            var info = await infoResp.json();
            if (info && info.Version) appVersion = info.Version;
        }
    } catch (e) { /* keep fallback version */ }
    var deviceName = getDeviceName();

    var request = {deviceId, appName, appVersion, deviceName, data};

    if (" + $"{isLinking}".ToLower() + @") {
        setStatus('Linking your account...');
        await link(request);
    }

    setStatus(ssoProviderDisplay ? ('Signing in with ' + ssoProviderDisplay + '...') : 'Signing you in...');
    var url = '" + punycodeBaseUrl + "/sso/" + mode + "/Auth/" + providerPath + @"';

    let response = await new Promise(resolve => {
       var xhr = new XMLHttpRequest();
       xhr.open('POST', url, true);
       xhr.setRequestHeader('Content-Type', 'application/json');
       xhr.setRequestHeader('Accept', 'application/json');
       xhr.onload = function(e) {
         resolve(xhr.response);
       };
       xhr.onerror = function () {
         resolve(undefined);
       };
       xhr.send(JSON.stringify(request));
    })

    var responseJson;
    try {
        responseJson = JSON.parse(response);
    } catch (e) {
        setError('Login failed. Please return to the login page and try again.');
        return;
    }

    if (!responseJson || !responseJson['User'] || !responseJson['AccessToken']) {
        setError('Login failed. Please return to the login page and try again.');
        return;
    }

    clearTimeout(ssoTimeout);
    setStatus('Success! Redirecting...');
    var userId = 'user-' + responseJson['User']['Id'] + '-' + responseJson['User']['ServerId'];
    responseJson['User']['EnableAutoLogin'] = true;
    localStorage.setItem(userId, JSON.stringify(responseJson['User']));
    var jfCreds = JSON.parse(localStorage.getItem('jellyfin_credentials'));
    jfCreds['Servers'][0]['AccessToken'] = responseJson['AccessToken'];
    jfCreds['Servers'][0]['UserId'] = responseJson['User']['Id'];
    localStorage.setItem('jellyfin_credentials', JSON.stringify(jfCreds));
    localStorage.setItem('enableAutoLogin', 'true');
    window.location.replace('" + punycodeBaseUrl + @"/web/index.html');
}

document.addEventListener('DOMContentLoaded', function () {
    main().catch(function (e) {
        console.log(e);
        setError('Something went wrong during login. Please try again.');
    });
});

// https://stackoverflow.com/a/25435165
</script><iframe id='iframe-main' class='docs-texteventtarget-iframe' sandbox='allow-same-origin allow-forms allow-scripts' src='' style='position: absolute;width:0;height:0;border:0;'></iframe></body></html>";
    }

    // Escapes a value for safe inclusion inside a single-quoted JavaScript string literal, including
    // the characters needed to prevent breaking out of the string or the surrounding <script> block.
    private static string EscapeJsString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}
