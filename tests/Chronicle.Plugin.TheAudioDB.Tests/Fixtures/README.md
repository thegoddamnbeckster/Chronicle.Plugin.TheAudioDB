Recorded from the live TheAudioDB v1 API (free key `123`) on 2026-10-14. Real responses, unedited, apart from the
`*_quirks.json` files, which are selections of whole records that show the quirks the design (docs/plans/2026-10-14-theaudiodb-plugin-design.md
in the Chronicle repo) depends on: an alternate artist name, a solo artist whose formed and born years differ, citation
markers (`[17]`, a trailing `[`) inside a biography, empty strings and nulls for missing fields, a `strTwitter` of `1`.
The two `*_none.json` files are the "nothing found" shape; `key_rejected_404.json` is what a key TheAudioDB does not accept
(or the retired key `2`) returns, with HTTP 404; `service_unavailable_503.json` is the shared-routing failure.
