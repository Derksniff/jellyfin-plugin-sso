using System.Globalization;

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
    width: 180px;
    max-width: 60vw;
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
  <img class='sso-logo' src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAPkAAABOCAYAAADigLBdAAAbd0lEQVR4nOydB3xUVfb4v3cmvTeSIIGEKiIL0iKIusiKuPyxgqhAEoRVFLFQ3QUsWFAXEVSwIYRMwIKwsoo/XWVFFBEFBKnSew2EkIRkkkzm/j/vzQQDZMqbkrDmff1gMvPuu/dM3px37yn3PAM6Ojp/aHQl19H5g6MruY7OHxxdyXV0/uD4Tckn8K36M5uKc+PMIo/NSB5hsb+G1dHRuQDhr47TuZPxLOYA+++PIO5RK9aSMxyfFEnC19cSRzv70MOYQwFHuJIbMVNEQ1rxA/NZxCR/iaajU6/wm5JPYy9lnG2fTMt1gQQZlffMlBzZwNL2iTQ7+TRd1Nl+Cj2YyXHiSKScSo6ynVAiWccScnnUX+Lp6NQbAvzVcRgxBBGaaiTAKO3vGQm4LIG05EBCT97B06qCz0dyhB19DBgHWyg/XMiJqVYsJ1Jo6y/RdHTqFX6zyfM5yCE2/VxO6T7ltURSgXnFZK7eUUohIUSqs/0BtnRKIHVJBPH3xtBwbCod5kykPRabLa+jo+MlfpvJ40ghlKhjh9jcJ4mWGZVUlOxhzTvPs6FcOV5JBdEkUU5p9wCCArHbDgEEXT+ShUmhRB33l2w6OvUJv83kW/lGVeJw4rYFETohiNDn42mSp8zgu/kJgYFiTlHMyV8rsajnSFX5yzfOZMBJZbbX0dHxHp853sbyBbtZTXv+H8m0IpxopItzlNk8nED2s3twDMlDyik9coCNk9LocCCCWE5zjOPs5GGu4ymyeY+hvhJXR6fe4BMln8T3nGAPPchkJR/Gt6VXYiTx0pWSVxOgrIJyg8BgDSTAaIVAATKPA3IxEw5l8ubZ4UTzOEuYwe2+EFlHp97gtZJnMgsDBl7iQT7iyLhwYscEENTAgBo1c1fPZQ2vRSUWWYF5dyHHxyXR/NMM/0X8dHT+sPhEa96mADNFg2JoOF9RbomUAlHuZf9SIoMNCMopK8ljT9cQIjc9TmNfiKyjU2/w2rv+JKvoTjSrEEONGDFTvOIsBY8kkFJs/V3JtSi7OqsbwHqCA2nRJM8LJDg1nPiB4UT/w1t5dXTqG14reTixatrKaoxRinZWUPZrI1I2lWBVPegSq6rjwg09t2JVl/4K5ZRzF6n7vqD4kKLkBgwxQt9Po6OjGa+VfA8/sYjWVGBeGUx45zBiHijGcrmRgLNAELBrO9+NbkE3OZRgh/1kU4GVykQDwa8DIQEEVS6jIjGY8O7KjaIC83dlBHorro5OvcNrJQ8jhh1sopDjLzWl8/WhRHU0YOxddVwitx/il9HN6OK0H0WRJdYIYEDV8t5IIFYsFJO/4HEaf/QPlnsrro5OvcPr9e8MbieaROJocnwlpr+c4uBwC+VTKqlYgU3JzSZGU5Xw4hw16ma2Yqm0UP5uGSVTjrOr/0iSMifynTWKRG/F1dGpd/jEyN3MMlWd0+hUEELEu0YCJ5Zw5jPsM7SChTIXvUj1P0WmSizly5j5bAWlE0OIXPwIi9RO9rLOF+Lq6NQrfJK7/jaDz/0+kFf5K6MQGIKqt5EuQuZCdbmpG9bUGHkj2oaeYA/PkO4LEXV06i1+26AC0u5Ol/b/W522tin573cC4ce97jo69Qk/Krk2JVWUXGA4N5PrOq6j4xt8Hnj+PR5um8mrlulWFzO5zSKvnu4u/Fm4Rken3uCH7BKbYl5sgTu3yaV6G6g814l76TM6Ojqu8MNMXtOvrh1vtjh5pYOudHR0PMVvM/nFSu5quW5V/5MXbUjT0dHxBj8mg59vk7tsff5MLvTFuo6Ob6gNm1z91bXjrcoml+d3pKOj4xW1ZpO7crzZlurWaifrrjcdHV/gR5v8wmQY97zrv7fTFVxHxxf4MU5+Pq6VXFbN5BfEynV0dLzBn1UY7I63KtzzrttPFUL9p6Oj4y1+dLxpXa7Li2xyHR0d77mElut6nFxHxx/4XMmb0YXK8xxottrM1vOz2S6iEgsWys+1l8hKpY+HWOBrEXV06hU+VfL7yVYfsjCEABFI8HXKe2WUbPgCKQs46vTc4+xiHC2Omyn6JYCgwHia9EykGd0Z6EsRdXTqHT5dD7/EVkKJopSiO5NpubgSS9le1qaHE7vRTBGT6erw3KnswkgAJRT0bESb/1ooP7qGxZ2SaXX0LKeZRh9filqrzEPOc3Z8CGJI7UnjHf+rnyUHmS2dfN8rYPT9iPzalap28Nl+8kxmqrP4q/Q1vk3BeANGCjnxfjrdNz5CK/az0+n5W/iaQ2zkGd78Zj3H50eTOPhKbpzYmMtGZiLoz/MsYpKvxK1VDJDl5PCeWhTFa1x8FlQ9vwQR0FVAa0fHo2Fc7UpUe/hsud6Om2lKZ6ays38IkVdXYC45yvZpG9nInUxzef48HqI9fVnOBg6wYXI55sJIEkb8yk/dnuQ3ruAGX4mqo1Ov8ImS38e7HGUHj5AcFEXieIP6WOL8+en02DKJ9pxgt1v9KO2epANtuWlXEXlTjQSKJFq+8AStxUn28wA5vhBXR6de4RObfDr7CSSEEs5kJdF8noXy4u183zmShO2nOaI+ojiSBBRFVWz2fazjEybX2NdIFhJBPCt4J3IIs9cEE3H5CXYPDSc2W3k/63/wKSomnD7gdU8monktiuMVLj4LmVyaOUwm5DacLNdDIHEAIq92paodvNaYYbzHYbYykqTQaJLGCnUWP5WTTq/tB/hVDandyO0M4VpuZhBN6UQ0DXmK1dzLKxf1N5MBNKINA3mt6DRHngZJDA2f+ZH3E3axGv0BCzo62vBayTtyK81IV2bzfiFEtLVQXnScXTO2s4l/MoQKzLccpiD3a05/f5T8OeWYb8hgOAEE0onbGcnHF/W5nqVs5D88QeuPzBR9HkJEk67cO7413XiRG/gbc70VW0en3uCVkmfwhupRH0F8cATx6ixeQsHc5ly9azvfR3xIwYJYGn0aRszgcGKvDSduaAJNvvmNvNlLeSnMQgV/ohcPs1DtT5nZJ7NWtSKuYSCvc5jTHJlYSUVFODGP72Vjh2nsJY0Ovvr8Ojp/eLwKoXVjIAaMvMahe4MIa2+h7Mwxds1oTjpXM+DFcGIuymQxEEAECX8bxuxmm/n6nigS8uJIoQf3k0hzwoimH8PYzv7hFZhXJtPy1xLOvBFJwugGpL4whOg+4/m3arvPVB+bVvvMQTYzQk8BfxLQEEgAjko4IuBgCMwegCj19bi5yOuscL0azIAGgFXAIQmHJayNg//ciijx9bj+ZgEy3QI3C2gGJANBEvYYbOHFVRmIb2tTntnIpGDIknCFXSYk5AOnBCyvgK+G+cB+n4dsZ4DrpS28p3yHwpRrKWzXc6sBvspAHPJ2HI+dJMoyO57G/IunQh7jk3VBhLUp4uTUIELHH2NHq0ZcuT6AoDBnfZRTsquQvL+/S8bnH/OduS3wLGubJ9J8fChRD5gpXhhL9N2r+TyhLX/ZEEBQo3wO3R1E2EIrFlaxgA8Y6+lH0IwJeTswXr2/OadEwmwBEzMRZ711vOUgxwm4D7jCxbhlwMsZ8IxAuFd3SyO+dLzlIEcDYwRc5qLpRuD1TMScD5CNK+CAo4YSPs9C9K1BbpeONzNEgJqMMdQN8RU7c3Ym4ms32p5HNrKfER4G13FhqdwL4MUsxA6t41ThsZIvRHKGEoo5NTKOlDcsmE/uYNVVHfnL4ZPkZUfSwK2kCKv63FLzToncDsQFENTOSKDyx1bz2Y+xo3cUDb4qo2REPI1nKTeGL5nR5Qp6FORzmDe5x9OP4DbvI5MsMB+4UeOphwSMl0oXjnGo5CZkojqBw01aBpWw0Qoj7kP8oFFel/hCyXOQfQTMAtI0Dv92IEzxh5IDw4G3PDBh5wt4OANR6E5jE1L53CM0jqGszh7ORDjNNnSERzb5QyxgE6v4lndjI4gdKxAUk/9WGh0Pb+HHTqFEDXJfAANBhLUMJrxvMOHXVCk46k6VAGJo+NzDJBpGkza7lMJ1wUS0uJbM8ZdzrargGbzhyUdwm2xk1wr4wQMFV0hxoeAOsSv451oVHNudW1kG/msusqUnY/uTHOQIYftcWhVc4cEKmOgHsRTe8VAfBktYb0K2ddUwFznXAwVHfUI4ZJuQd3lwrvYP1Y/nyGAgqXSgC/3vDyYitYyS4zv44c1EYkmk2bgAggM9EaYmQolKn8qu+2dyrOIUhyYps3sUSY9vY0W7KWyiiWqe+gcTsqkBFguoizj2K0BnT08WkGiEOb4VyTtMyCH2GdwbhvtIHF+i2O2L5yIbO2pgQo6UNpPLGxYsQCZoPUmzkieQyn/4iZ9Z2CCCuEeV94o5+VYbeh7byE9XhxHTT2ufzjBiJJrECWv4V/xVtP3yLPkfBxIc2og2z42hLcfYqcbq/cQCN+xFn5OD/LMavPASAdfNR/7VN1J5Ry6yq7ow+uPSykjN+6KfRgZIeNEHYwRa4DGtJ2lW8pfIII2OXEHPh4IIa1RG8ZEtLHurEQk0oOnYAIJ9+hBFxQAMJqJJB24Zd4RiDrP1qQrMxeHE3foqW/v9id5c6dFK2jkmpDsONr8gwKm5I2GrhOcFvAz86KxtpWImXwJY4YW6lsHfKDfVHOT9F77fAgYJm1OvRqTNYTpNwFPAIhdjaF4NaFLIkSxkOj+Sx77LOnLbCImkiJOzrqLvifX8fE0q7e/QKoA7KDZ/BPEj9/CzKY2OW4vImxFH40lxpDw3l6Ff3cCIogdZwNvOdcNtspEhdi+6O2yzwhcGKLSC0QANJNypLJe9EOFaJ8cOmKHL8GqhMhdOpTqfyXORt0ro6WbzpQK22ENWDYFroE4eUp+r/K0llEuIFjBAQIqrkwQ8sxyZfQPCUvWexMkea9vxm7IQ31W9zkHOdaLMjUzIxEzECXc/iCYln8kA5QtFLCkjgwlLMlN8cC2fvDOYUcphxRY3aulPC0GEhl/GFU+nEHX3e7w67WbGDAwl8op7eHVUHI2fzcLAXUzhYyZ4PZYRHgDi3Wj6WCbi9RrefygHOVXgcXwv1Zl4wy+Ohc+6cFUmba+Nwl5ppy6xwihXLncJa4CsLMS2C4+ZkLcAb1MLppOEzwLhwYGIIxccGmNCvqL8dNHFZQfhNsVGr9ZnmovPf6r6CwNkS9hwgVwGAxiUiSTU5m13G7dDaCP4gESacZrDTf5E718CCY0/yb7xQYRNzefgdY1ptyKAIL9uTrBQzhG29Y4k4asyzmYk0sJkoaxoJ6vSI0n47Syn1bRXb8lBfiWgl6PjEk4Y4LYMxGpn/ZiQ/Vwtv2oKoZmQR+1JIY7GfyULUSf7n7WG0LKRyUaclwWSsGAPZE5GOCzpuxDZwGyLVDi1zbwIoSnnfpWF6O2sTS6yv6SGXOwLmmUiMquN/SFwt5NxvxAwRMvsrAW3bPIMXudN7qUl6TSly6NBhMWXUbx3JTmzW5NEAqnj/K3gqMuOIOJJnfwYKcZxtMot4fTyIEIjG/OnZwfQnoNs5B6mejVGDjLemYJjuzM+60rBsX3hF0t41wMxVrgYf6xyI8hBvuxO6KYuMcAtLpqYBYx2puAKAxB5lfCMb6X7HQk/uVJwVF0Qyk3badxW2mby6qx01l7AXyUcMyHn5SL/4q7M7uKWkseTquaU/8IXzSKI/5tiixeS93pPHiz4kfV/DiXa1YX0GaFEdp3KzqGvcYg89j5podwaTtxdi9jctzN30Jk7vepfwOUummzIRLgdBhLwpGLXaZFBKpaRa5KFzW+wyYT8JQf5nAl5tZZxagMBHZ0dt8Jz7s5g9uSe2T4T7nyecLdhiK3tMUfHBURlI2OqXleihn+cFlUQtlV1loRlJuR+E3JWDtInNc/cUvIZ3EZrOtGE9o8HERpdRvGupUyZ055k4mk8XplhawtbSC154hoWJ7Sk8w/FnJpjJIA4Ul54nCahJ9itOgi9wKktLmGdls7sX+CtWs7JQqyUONhwXzMdhC0dc7UJuSoH6XBpWAe48m0s09KZgO+8E6dGrFkIp6un6tj3JVzkO7iAc+bWfQizhMc1yNNEsZAFfG5CHshFjl6I9Ni34lLJx/Elz/Mr61neMoK4++we9en9eL7oOzbeEEZ0rVZYtIXUwlO7cOeYMxSwh5+fL+PsyVCi201jz2OXcz1v4FFiUBVOv5QCtnjQp+ZzshDK0nSKB2N1E/BhDnLdPGRdeKXPQ9o2XjikEvZq7FLTDdNNXCms5nMMkFT9dRZiqbTVv6vQOE5jCdNK4UgO0lV9PUeyOGYQ05nKzbSmHY1oMzqQkIgyirc9S7N53UhWZs8njPgsuc1tBIJIGozczy9tWnDNgULyXkYtxpc8fhvLm7/Ar0xwbtY6RLp4/KqEYg+69egunImYKOAurasH7MtkA6wwIa/xZGwf4tRXI8GspbNKW0zZpzir4uoEp7pjqEHOLESOFTpL+EzrYAISBcwzIUf6VNAE0niRLfzKqjbhxGTaZvFT057naMnXbL0xlEiXjgp/odxwUmj7VAoNGEPTWaUUbgwmNDaVDpO7057d/MiD6p4SzbjaQthQa4dCzYfwjAzEoiyE8sW4WcIHGu37EDxz/PkSp/a2wYXHu4b2Ps/HF9DmNWSwxtPaOzso4XhN7w9BbMxC3GqF9hJel3BhqM4Vb2j1vThU8ruYQjZ30I42JNF8bADBYWaKN4+h6fwbSSaGhuPrYhavTjixd29iQ6/p7C89zeEnLViIIG7QSjbclG4vI6UVg2sl15RcMg/Z3Jsc9CqyEP/JQgy0QrSEYRLc3WN95TzkYG/H9wKnSh4IN2vpTILPvc8Ksbj/FI93kGESrnLWJsSJY47flf2xLEQjAX2BjzSIq6nstUMlT6MTf2czq/nhqjBiBlqxUsjxV17nSNkStvYOIdJpmKk2UG4yCaROHkWq4RbafHqW0/8OIJAGpL0whmbBpzmixve1sAvWS7A4Oi6gaw5Sy04i77NzqnEfwpyFmJuFuMGAWiLHZb1ro0ZF8jHrnR2UMMHdTRfZyDQBmper7iDhXnfbhsJzQv3hsK99WoqGZCA+z0TcI+Ay4V6mpaZolkMl384KunMlibQYF0hwsJmiDeNo+f4jNFRm8SeMvnsug1eEEtXtn+wYtozDHGX7U+WYS0OJ6vwKux9qzfU8oHG/+WRbOqKrapHT5yFdZt3kIvsb3CtA4BGDERsyEWOVmd1ZO2mrIlNXOI0RK5OeBV5ypyMjPOsbkS5GQC8T0uUMmYvsAYx20ZfmQhLYlP1oBmIq0N1ZO63Xs0aHwyg+JYkWlHAmPY0OPxgICDjOrsEhRC4o4XTfZC7/7FJRcuUDlFK07zvmderJ8PxC8qbG0WhsGSV5m/mqSywp+0+xT5PHfR7yCYPrL55ZwCMZiBq3wJmQY9WnP7nmvIw3e5bcdPtqwiJsPyuqrS6UZft5q4OnkYZmUCpwGMtclonwycrLk6IROchNApwm7Uj4WsDgmmLmucjLrZAjwKUt6k3Gm/38Fy/8+1br4wncuCFZ4e4hCDWO+3/I4DzYpVzHqmtqv67KNZUCCjMRF00YOcj/Cgf5/ko/WQi3beUaNbWQE9zNLazlxOhAggNKKPz5CVp/JJG8TdjYS0XB+X2XWlo6/UeXUzJpA5/98xoG9Qshsmlzuk68nOQHPlCLQ7pPHLxRYCtOEOmkWYiE2TnIYfZv9Xx7bbIkYbPvHO4tdsH3yrkXakq113H/h5zcB3HOe9sU/uxEwZUv3SkPZfEJBpgpbbnnDrFnGe7NQWYDWwXkC0iyQhdp28VVKwj4Rw6yt4ClEo4aQFZCvAF1lnBqh9vZU6XgCsp1yrHFuFMcXdP5yL6DEUur3lfMF4vzm6Km+nI1amszupDN/Mh0+neTai220vfe5JTlVfb2iafxn7UMUBtU7VI7yKbcbgzcXkbJmyFETg0jupeJ6WFX0FNTQv+tiJIc5Ezlgrse+9wOo6744GkVykxmQu7EsRc57ST8YkLOt8I+g212c1pIwWC7cdQZGYh3TMgJ9iQPZ4QJW+0zFVlHT6i3Z+l1FHYZtOzHFjVsqRXwhbO6cZWwJAc5zQDrJAy1QHMXuxg1FbasUX4rVmJIjjFgjLRSiZXKw0GEEEmD7oZLaBavjpHA6HBib6ikggCCf5NYlfciGtM+RnhQ1ce+ZHO6V9uPvObieBtgisG2YeMxe6isRiRUCtu2yTpFeFb26H+NZRmIix4K4KoajrDtFhwvbR723q5CrkKxHDRQ47c/gCAsVBRJZInBlsfRqhILZ8lfY8VSJ3dXZwjV0CmzmCleq8zqFZS0sim2LLVSWRzoWAecUgF/k3DW5wK7wJ4b/5sv+hIwwd0ig/4kA/E58Fxdy+FHDgQ4eOJrBuIXCR/6aJxFmYgvtZxQ47R8mK1MpH/BpxRuCSSoUQgRA4cT/dpC5JLDHHwghIh+uMgMq0WERFoKOfFeNElrHyLe+A4FA4VqZpg3DaVX4QzPaikyDLE1F3mj1VbnzaO9zFJdjbHWHadRdQQMssJKZ6EaN1iYifinF+f7lEzEU/OQlQYvdpNJmCNcRBM8YL19z3esh+cfFpBVwx70c+yBQc1tdQK8qTa0TUMxk3PUqOTRJPIJpyijZG4wETeFENnlDY5OuwtGv0vM7CZEzq7zSgR2FGU2AweR9FbTzo++HEJUJ8XIOEt+zn8pI1Z7kto5MhCrc5A32sv1avZHSBhogP5oVHLl7p+L7C1tDj1XtmxN4y7IQtRlEkyNDEFMNiFPSXhWq1JJ+CwIJlf4WMkllAvbLPyx6sfVxlIBD2QgnO6Zn4ywzkHeFmgznTRnikpYZY8+aM31r1nJt7MSiWQKPRa+TcGgMKJviSJp1GyK2pVRnL2XilO+fLa5D5AWyqOWUZgZRVIfZcleSuFno0n7eALfstNL09peraSHCXmf/U7qTirmF1aYOgSx3ITs78m4GYjv59lyz0cb4BFbgphLlkt4Nauat/ZSIxMxcwFygcWm6EPtJYcdYq+B9vcsxIwPnFRE9VKmz3KR7aXNfnaZVSfhWwGvZSKWuDuG/akrN5uQoyQ8KtwrS62YbbOyEO5sP64Rh+b1S2xDYuUQm+PbctOiMKJ7GBCXzBq9JoTdaXiWgm/WseSuZqTnGwng7y4fPKINe6GGPlZoY6/7lSIg3/6ImzUC/p2B2O7LMRciQ0vhDvtyr4n9X7yAk8pCRtqchCuyEHXlLPSIhUijGW6XtnThRtj+nhHYHjmlLH+XlMInNZS88hs5yA7CVgCzhT0kGiHhtIACCd8EwpcDEZ7sRjyPXORNVuglfr+eTaQtdHjSfj1X+eJm7VDJB/Iq6QyglDP8gytDpnPg4XBiRxoJSLuE7PHqiEos+86S/9ooUt+awuayUCJZw2Led56gpKOjo6Oj87/LpWRX6+jo+AFdyXV0/uD8/wAAAP//S6DgIIzW2yUAAAAASUVORK5CYII=' alt='ds labs'>
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

        return Base + @"
var ssoBaseUrl = '" + punycodeBaseUrl + @"';
var ssoWebUrl = ssoBaseUrl + '/web/index.html';
var ssoProviderName = '" + provider + @"';
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

    const url = '" + $"{punycodeBaseUrl}/sso/{mode}/Link/{provider}/" + @"' + jfUser;

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
    var data = '" + data + @"';
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
    var url = '" + punycodeBaseUrl + "/sso/" + mode + "/Auth/" + provider + @"';

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
}
