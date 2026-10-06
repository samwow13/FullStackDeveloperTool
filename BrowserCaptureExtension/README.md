# Browser page context for Snip

This Manifest V3 extension lets FullStack Launcher capture live HTML and CSS from the selected Chrome or Edge tab. The launcher keeps the capture with the note. The note's separate inclusion choice controls whether that saved source enters a queued prompt.

## Install and pair once per browser

1. In the launcher's Notes & queue page, open browser capture setup. Copy its pairing code and open the extension folder it provides. The source checkout's `BrowserCaptureExtension` folder also works.
2. In Chrome, open `chrome://extensions`. In Edge, open `edge://extensions`. Turn on **Developer mode**, choose **Load unpacked**, and select the extension folder. Keep the folder in place after installation.
3. Check that the installed extension ID is `poppjfplkbpgbabkabcbhdaifmbfcijf`. If it differs, reload the supplied folder rather than a copied or repackaged manifest.
4. Open the extension's **Options** (or click its toolbar icon). Paste the 64-character pairing code and save. Repeat the installation and pairing in each browser you use for Snip.

The extension asks for access to web pages so Snip can capture an already-open tab without a second browser click. It connects only to the launcher's local broker at `127.0.0.1:47873`. Before reading a tab, it verifies an HMAC proof from the broker using the pairing code. It never sends the code over the connection. The launcher checks the extension's origin and its proof in return. If the launcher is closed, the extension waits and reconnects when a browser tab or window gains focus.

## Open frontend after build

Frontend service cards can opt into **Open after build**. The same paired extension checks all tabs in each open Chrome or Edge profile for the local frontend site, including application routes and local host aliases at the same scheme and port. An existing tab stays untouched. When no matching tab exists and every browser window has a complete paired inventory, the extension opens one tab. If a profile is unpaired, a tab cannot be checked, or browser state changes during inspection, automatic opening is skipped. Incognito windows require extension access; otherwise their incomplete inventory also prevents automatic opening.

The extension returns only whether a matching local site exists, window counts, and inventory revisions. Tab URLs, titles, HTML, and CSS are not sent for this operation. Requests expire quickly; cancellation removes only a tab created by that canceled request. Reload version 1.2.0 in the browser's extension page after updating the extension files. Pair every open profile that should permit automatic opening.

## Fill saved website logins

Website shortcuts can select a website credential profile saved in Windows Credential Manager. Choosing **Open** opens or focuses the saved login page, prepares its login fields, and then fills the username and password. The launcher does not click a submit button, send an Enter key, or call a form submission method. The website's own scripts can read filled fields and respond to input/change events, including submitting credentials or signing in automatically. Review the resulting page; use saved credentials only with websites you trust.

Website shortcuts can choose an installed browser. For login filling, choose Chrome or Edge and install and pair this extension in that browser. The launcher waits briefly for the selected browser's extension to connect. Other browsers do not receive the credentials or affect this selection. More than one connected profile with login support in the selected browser requires closing its extra profiles; credentials are never broadcast. With **Automatic**, ordinary links use the system default browser, while login filling retains the existing requirement for exactly one connected Chrome or Edge profile across both browsers. An existing matching tab must also be unambiguous. Login fill is separate from automatic frontend opening and does not relax that feature's complete browser inventory requirement.

Credentials are bound to the exact saved origin (scheme, hostname, and port). HTTPS is required except HTTP on `localhost`, `127.0.0.1`, or `[::1]`. Redirecting to another origin requires editing the shortcut and choosing a credential profile for that actual login origin. The extension fills only the main frame of the exact document prepared before credentials are sent. It rejects changed documents, cross-origin form actions, unsafe submit-button overrides, and forms that submit with GET. Embedded-frame logins, Shadow DOM logins, multi-step username/password flows, and forms without one shared HTML form require manual sign-in.

Optional username and password CSS selectors let the shortcut identify one visible, editable input of each type. Without selectors, the page must have exactly one visible current-password input and an unambiguous username/email input in the same form. Hidden, disabled, read-only, signup/new-password, or ambiguous fields are not filled. A website that rerenders its fields during preparation may require retrying after the page settles.

Only fixed outcome tokens return from a login request. Passwords are never saved in extension storage, added to tab URLs, logged, or returned as page context. The launcher's temporary password serialization is erased after transmission; the extension releases credential message references after filling. Browser JavaScript and the destination page necessarily receive the transient field values. Snip retains its existing form-value removal, and capture/site-opening requests cannot run during login preparation or filling.

## Capture scope

- Snip triggers a single capture of the selected, active HTTP or HTTPS tab. No page source is sent by the extension outside an authenticated capture request.
- HTML is a snapshot of the main frame's live DOM. Scripts, inline event handlers, hidden inputs, current form values, and named credential attributes are removed from a copy before serialization. Other visible page text and attributes can still contain sensitive information.
- CSS includes accessible stylesheet rules, including inline and adopted stylesheets. A cross-origin stylesheet can block CSS rule access. Embedded frames and Shadow DOM contents are outside this snapshot. The launcher marks these cases **partial**. HTML and CSS are each capped at 200,000 characters; truncation is also marked **partial**.
- Browser internal pages, PDFs, and pages where browser site access is blocked cannot supply HTML/CSS. The image can still be snipped, with source capture marked unavailable.

Keep the pairing code private. Use the note's inclusion choice before submitting a prompt containing page source.
