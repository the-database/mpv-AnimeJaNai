# Frozen compatibility fixture

`counter-api-1.0.ajnaddon` is copied byte-for-byte from the previously built
AnimeJaNai-Addon-Foundation-preview.3-win-x64 bundle. It contains the old compiled
counter0.2.0 and API1.0 JSON-only SDK, not a rebuild with the current SDK.

Package SHA256: `a3aa62100ff02639763294c4963e7ad335db78052a7b58c9def1617857e21317`.
Module SHA256: `e905ec14db6cf281d59aa79eedd6c8bc15beec90e684bc655cf511de0c5b7c2d`.

The runtime test checks old handshake, storage, settings/actions, reconnect and
saved-state behavior against the new host. Keep this fixture unchanged as the API
evolves. AJN source uses the repository license; embedded Javy/QuickJS notices are
included under `addons/licenses`, as in the original developer bundle.
