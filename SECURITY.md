# Security policy

## Reporting a vulnerability

Please report security issues **privately**, through GitHub's "Report a vulnerability" button on this
repository's Security tab (it opens a draft security advisory only the maintainer can see). Do not
open a public issue for anything that could be exploited before it is fixed.

Include what you found, where (a page, an endpoint, a file), and how to reproduce it. A proof of
concept against your own account on your own box is ideal.

You will get an acknowledgement **within a week**. This is a one-person project, so a fix may take
longer than that; the advisory thread is where you will hear about progress.

## Scope

- The application: everything under `src/`.
- The deploy kit: everything under `deploy/` and the runbooks in `docs/deploy-*.md`,
  `docs/family-cloudflare.md`.

The things this repo cares most about are named in `CLAUDE.md`'s review gate: the tenancy boundary
between households, anything written to disk per household, new settings keys, and new endpoints.
A way for one household to read or change another's data is the finding that matters most.

## The demo box

https://demo.shelfaware.net is a shared, rate-limited public instance that real people use. If you
test against it, **stay inside your own household**: register your own account, use your own data,
and do not try to reach anyone else's. Anything that needs load, fuzzing or many accounts belongs on
a box you run yourself — the deploy kit will give you one in an afternoon — not on the demo.
