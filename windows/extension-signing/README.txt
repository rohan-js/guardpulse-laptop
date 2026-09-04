Site Guard extension signing key (guardpulse-block.pem).
Committed on purpose: the extension id derives from this key, so every
build must produce the SAME id for the force-install policy to keep
working across updates. Never regenerate; treat like a password.
