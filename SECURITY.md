# Security policy

## Reporting a vulnerability

Please report it privately, not in a public issue: open the repository's **Security** tab and choose **Report a
vulnerability** ([direct link](https://github.com/selcukgural/NPlusOne/security/advisories/new)). Say which package and
version, what an attacker or a careless input can make happen, and how to reproduce it. The fix is prepared in a
private advisory and published with the release that contains it.

## Supported versions

Fixes go into the latest release of each package (`NPlusOne.Analyzers`, `NPlusOne.EntityFrameworkCore`,
`NPlusOne.AspNetCore`). Update to it to get them.

## What counts

- **The runtime guard** promises that reports, logs and exception messages never carry a value from a command (a
  literal or a parameter), that it never breaks the command or request it observes, and that it costs nothing outside a
  scope. A way around any of these is a vulnerability: for example SQL whose literal shows in a report, or an input that
  makes the guard fail or stall a request.
- **The analyzer and its source generator** run inside every build and IDE that references them. Code or a referenced
  assembly that makes them crash, hang or report wrongly on purpose counts too.
- **The release pipeline**: anything that would let someone publish a package that was not built from `main`.

Out of scope: `tools/scan.sh` builds the solution it is given, and building runs that solution's own MSBuild targets;
scan only code you trust.
