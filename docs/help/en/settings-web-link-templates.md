# Web link templates

Each tab is one web link template; every template becomes one menu entry in the
"@" menus for both a project and ROOT in the main window.

Clicking a template's menu entry renders its start URL against the project's settings and opens the result as a new web page tab in the app. The %gitLabRepoId% and %teamCityProjectId% placeholders are substituted from the project's GitLab repo id and TeamCity project id (edited in the Projects section); whenever either one resolves blank on the project, the whole rendered URL is discarded — unconditionally, not only when nothing else is left to render — and the template's site root (scheme and host only) opens instead. Any other %placeholder% name makes the whole template fail with an error message instead of opening a page.

From ROOT, a template without placeholders opens its exact URL. ROOT has no
project ids, so a template using %gitLabRepoId% or %teamCityProjectId% falls
back to that template's site root.

The rendered URL is captured once, at the moment the web page is created — it does not update later if the project's ids change. Open a new web page from the template to pick up new values.

Start URL must be an absolute http or https URL (after substitution, or as the site-root fallback), or the click fails with an error instead of opening a page.
