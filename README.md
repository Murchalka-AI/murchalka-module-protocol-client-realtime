# Client Realtime Protocol

Authenticated loopback WebSocket bridge for universal Murchalka clients. It exposes no product state and invokes only bound Auth, Agent Runtime, Agent UI, and declared `client.action-handler` capabilities.

Phase 7 adds `action.dispatch`: the bridge bounds untrusted identifiers and payloads, resolves only the requested provider among the granted optional action-handler dependency set, preserves actor/idempotency/deadline context, and returns the provider's schema-validated result. A client cannot invoke an undeclared or ungranted module capability.

Tags in canonical `vX.Y.Z` form publish signed immutable bundles for all supported RIDs.
