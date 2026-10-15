# Security policy

Fieldkit installs keyboard and mouse hooks, reads clipboard history, controls audio and power
settings, and can run installers and uninstallers when you ask it to. Security problems are taken
seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/BJDevelopes/FieldKit/security/advisories/new).

Include what an attacker can do, the steps to reproduce, the Fieldkit version and your Windows
version. You will get an answer as soon as possible; fixes for confirmed issues are released before
any details are made public.

## Supported versions

Only the latest release receives security fixes while the project is in preview (0.x).

## Scope

In scope: anything that lets untrusted input (web content, calendar feeds, file names, clipboard
data, other apps' windows) make Fieldkit run code, open unexpected targets, delete or leak data,
escalate privileges, or bypass Windows protections such as SmartScreen or the Mark of the Web.

Out of scope: problems that need an attacker who already controls your user account, and the
documented, opt-in behaviour of features (for example, cleaning mode blocking the keyboard).
