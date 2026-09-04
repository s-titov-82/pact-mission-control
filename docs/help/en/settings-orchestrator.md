# Orchestrator

The orchestrator is one dedicated Hermes session pinned above projects and
ROOT. It can inspect session status, read retained agent messages, report
subscription usage and active review runs, and send a prompt to another live
session when that session is not controlled by a scenario.

**Initialize** asks Hermes to create the Pact profile, installs the Pact MCP
connection, SOUL and status-report skill, writes the endpoint and credential to
the profile environment, and then saves the launch configuration. Every
provisioning step is shown separately. Existing Hermes configuration is
preserved semantically, and Pact backs up files before replacing content it
does not recognize as its own.

**Reissue credential** invalidates the stored orchestrator credential only
after the Hermes profile has been updated successfully. **Save section**
persists the enabled switch, workstation lock detection switch, and both prompt
texts. Lock detection remains separate from enabling the slot.
