# ADR 0001: Authenticated loopback realtime bridge

Status: Accepted

The protocol module binds only to an explicit loopback IP address, authenticates every WebSocket before actions, limits every message to 64 KiB, and invokes only Runtime-granted product capabilities. Product authorization remains inside Agent Runtime and Authorization modules.

