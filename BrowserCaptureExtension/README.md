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

The extension returns only whether a matching local site exists, window counts, and inventory revisions. Tab URLs, titles, HTML, and CSS are not sent for this operation. Requests expire quickly; cancellation removes only a tab created by that canceled request. Reload version 1.1.0 in the browser's extension page after updating the extension files. Pair every open profile that should permit automatic opening.

## Capture scope

- Snip triggers a single capture of the selected, active HTTP or HTTPS tab. No page source is sent by the extension outside an authenticated capture request.
- HTML is a snapshot of the main frame's live DOM. Scripts, inline event handlers, hidden inputs, current form values, and named credential attributes are removed from a copy before serialization. Other visible page text and attributes can still contain sensitive information.
- CSS includes accessible stylesheet rules, including inline and adopted stylesheets. A cross-origin stylesheet can block CSS rule access. Embedded frames and Shadow DOM contents are outside this snapshot. The launcher marks these cases **partial**. HTML and CSS are each capped at 200,000 characters; truncation is also marked **partial**.
- Browser internal pages, PDFs, and pages where browser site access is blocked cannot supply HTML/CSS. The image can still be snipped, with source capture marked unavailable.

Keep the pairing code private. Use the note's inclusion choice before submitting a prompt containing page source.
