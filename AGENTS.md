# Contributor guidance

- Bind only to configured loopback addresses and never expose the realtime endpoint directly to an untrusted network.
- Authenticate each WebSocket before accepting product actions.
- Invoke only Runtime-granted capabilities and preserve actor, scope, correlation, deadline, and cancellation.
- Keep one declared type per C# file, align namespaces with paths, and document public APIs in English.
- Run formatting, Release build, tests, conformance, packaging, and Runtime verification before release.

