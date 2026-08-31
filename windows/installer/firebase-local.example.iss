; Example local build config for the installer. To build with the real key:
; copy this file to firebase-local.iss (which is gitignored) and fill in your
; value. installer.iss includes firebase-local.iss when present and falls back
; to a placeholder API key otherwise (the wizard then pre-fills a placeholder
; and generated agent-config.json will not authenticate).
#define FirebaseApiKey "REPLACE_WITH_YOUR_FIREBASE_API_KEY"
