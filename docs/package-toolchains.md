# Package toolchains

`eng/toolchains.json` owns the exact development versions and supported package-runtime
majors for a Sunder release. Run `node scripts/toolchains.mjs --write` after editing
it; `node scripts/toolchains.mjs` checks the generated files and CI pins.

- Package Maker uses the running Host's exact .NET SDK and Node/npm versions, not a
  floating LTS alias or the newest version in a vendor feed.
- After a Sunder update, Package Maker replaces previously installed managed
  development tools. The replacement is verified before old versions are removed;
  active builds retain their tools until they stop. Failed updates preserve the
  previous installation. Optional tools are not installed for the first time by an update.
- External installations are read-only. They can be reused only when their effective
  SDK or Node/npm pair matches Sunder exactly.
- **Upgrade project tools** updates local development pins. It does not retarget
  projects, change package dependencies or rewrite shared parent configuration.
- Worker execution runtimes are separate from development tools. A package keeps
  its compatible runtime major, provided that major remains supported. Runtime
  servicing does not cross major lines, and SDK cleanup does not remove Worker runtimes.

The initial supported execution majors remain .NET 10 and Node 24. Adding another
major requires qualification, then adding it alongside retained supported majors.
Archive recognition and historical receipts are independent of current execution
eligibility; an unsupported runtime is not made executable merely by retaining its receipt.

Avalonia package UI runs in the App process. Its compatibility with a new App runtime
and shared assemblies must also be tested before adopting a new .NET major.
