# Third-party notices

These upstream notices accompany the example's embedded JavaScript runtime:

- `Javy-LICENSE`: [Javy 9.1.0](https://github.com/bytecodealliance/javy/blob/v9.1.0/LICENSE.md), Apache 2.0 with LLVM exception.
- `rquickjs-LICENSE`: [rquickjs 0.12.0](https://github.com/DelSkayn/rquickjs/blob/v0.12.0/LICENSE), MIT.
- `QuickJS-LICENSE`: [QuickJS-NG revision 433941b99fb3c5e7f98b7ebd78727972bcf467ee](https://github.com/quickjs-ng/quickjs/blob/433941b99fb3c5e7f98b7ebd78727972bcf467ee/LICENSE), the submodule pinned by rquickjs 0.12.0.

The bundle script also copies the official Wasmtime archive's license and the exact published .NET runtime package's license/third-party notices. It includes buildable AJN addon source and the repository's existing license. Javy's compiler executable is downloaded separately by the opt-in bootstrap script.
