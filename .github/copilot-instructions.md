# Copilot instructions

- This workspace is a Rust port scaffold for WGL2Bridge.
- Keep the implementation async-first and zero-allocation in the packet path.
- Prefer `tokio`, `serde`, `clap`, and explicit buffer reuse.
- Keep Windows TAP handling separate from tunnel encapsulation logic.
