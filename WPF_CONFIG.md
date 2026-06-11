WPF version configuration files belong at the repo root.

Required files:
- `.env`
- `credentials.json`
- `token.json`

This repository now includes `.env` as a local template file.

For Google Calendar:
- Put your OAuth client file at `credentials.json`
- Put your authorized user token at `token.json`

The WPF app reads:
- Toggl and ClickUp secrets from `.env`
- Google Calendar OAuth data from `credentials.json` and `token.json`
