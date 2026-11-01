# Frozen compatibility fixture

`counter-api-1.0.ajnaddon` is copied byte-for-byte from the previously built
AnimeJaNai-Addon-Foundation-preview.3-win-x64 bundle. It contains the old compiled
counter0.2.0 and API1.0 JSON-only SDK, not a rebuild with the current SDK.

Package SHA256: `a3aa62100ff02639763294c4963e7ad335db78052a7b58c9def1617857e21317`.
Module SHA256: `e905ec14db6cf281d59aa79eedd6c8bc15beec90e684bc655cf511de0c5b7c2d`.

The original JavaScript, API 1.0 SDK, and compiled package manifest are now in
`counter-api-1.0-source/`. Rebuild a separate review package with Python 3 and
the pinned **Javy 9.1.0 Windows x64** compiler:

```powershell
python addons/tests/fixtures/rebuild-counter.py addons/.tools/javy.exe counter-review.ajnaddon
```

The script verifies the compiler hash and preserves the original source bytes.
Do not overwrite the frozen fixture. Recompiling the original sources produces
the same module size and code, but some initialized runtime data differs between
builds, so the rebuilt module and ZIP have different hashes. The committed
fixture remains the exact old binary used by the compatibility regression.

The runtime test checks old handshake, storage, settings/actions, reconnect and
saved-state behavior against the new host. Keep this fixture unchanged as the API
evolves. AJN source uses the repository license; embedded Javy/QuickJS notices are
included under `addons/licenses`, as in the original developer bundle.
