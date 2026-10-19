# Security policy

OpenIMG handles private photographs and an authenticated browser session locally. Do not include passwords, cookies, access tokens, signed provider links, full browser dumps, private photographs, or database files in bug reports.

To report a suspected vulnerability, use the repository host's private vulnerability-reporting feature when available. If the host does not offer one, open a minimal issue asking for a private reporting channel; do not post exploit details or secrets publicly while waiting for a response.

Never commit credentials or runtime account state. The WebView2 profile, settings, database, logs, and generated operation data belong in per-user runtime storage, not in source control.
