# Local build configuration

Place the Google OAuth desktop-client JSON at `GoogleOAuthClient.json` in this directory before building.

That file is embedded as `GLook.GoogleOAuthClient` so end users do not have to select credentials. It is intentionally ignored by Git and must never be committed. A desktop client identity is still extractable from a distributed executable; Gmail access remains protected by each user's Google consent and DPAPI-protected user tokens.
