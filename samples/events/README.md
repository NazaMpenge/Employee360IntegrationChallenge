# Sample events

`POST /samples/publish` queues the numbered files in order (one event per file).
Files in `manual/` are published one at a time with `POST /samples/publish/{name}`.

| File | Scenario | Expected outcome |
|---|---|---|
| 01-valid-update | Normal event | Processed, one call to Employee 360 |
| 02-duplicate-of-01 | Same event delivered again | Duplicate - no second call |
| 03-missing-employee-id | Required field missing | Dead-lettered (Validation) |
| 04-location-alias-cpt | `CPT` instead of `Cape Town` | Processed, sent as `Cape Town` |
| 05-newer-update | Newer update for EMP10452 | Processed |
| 06-older-arrives-late | Older update arrives after 05 | Superseded - not sent |
| 07-malformed | Truncated JSON | Dead-lettered (Parse) |
| 08-unknown-location | Location not in the alias table | Dead-lettered (Validation) |
| 09-rejected-by-downstream | Employee 360 refuses EMP66666 (422) | Dead-lettered (Downstream), not retried |
| 10-valid-after-bad-events | Good event after all the bad ones | Processed - the pipeline kept going |
| manual/11-fault-demo | Use with `/admin/faults` on the mock API | Retried, then processed (or dead-lettered if faults outlast the retries) |
