# Web monitoring rules

Each rule matches loaded web tabs with its URL pattern and polls the page
through declarative DOM extractors. Rules are checked in file order; the first
enabled matching rule owns that tab.

The optional Activity extractor reports whether work is currently in progress.
The optional Revision extractor reads a stable value whose change can mark a
background tab unread. Each extractor uses a CSS selector and a source
(`exists`, `count`, `text`, or `attribute`); text and attribute sources can
apply a regular expression and capture group.

The two starter rules are disabled examples. Their placeholder hosts use the
reserved `.invalid` domain and cannot be enabled unchanged. Replace each host
and verify the selectors against your own authenticated TeamCity or GitLab
pages before enabling the rule.

**Test on current tab** evaluates the edited rule once against the currently
loaded web tab and reports the URL match, activity, revision, or error. Testing
does not save the section, clear unsaved edits, or change live monitoring state.
