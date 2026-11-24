# Project Board manual checks

Use an isolated `--data-root` and a disposable project. Keep a terminal running
while using Board. Automated headless tests cover controls and saved state;
they do not certify drag gestures, native focus or visual layout.

1. Open Board. Verify five columns and exactly one Create button above them.
   Create a card, write its closure condition, and inspect Markdown preview.
   Close the drawer and verify focus returns to its card or Create.
2. Narrow the window. Scroll the columns horizontally; the drawer stays inside
   the project surface and its fields remain reachable vertically.
3. Drag cards within Ready and between columns. Drop a card in a gap between
   cards and in the space below the last card. Both drops append to the target
   column. Check that a full column has space beneath its last card when scrolled
   to the end. Verify the Next marker follows the first Ready card. Repeat with
   Move up/down and the status selector.
4. Open the same board from two project agent sessions. Read one order revision
   and concurrently call `pact_take_next_card` with it: exactly one succeeds,
   the other conflicts. Repeating a successful token must not claim another card.
   Empty Ready returns Empty and leaves Backlog untouched.
5. Type an unsaved description and append a comment from the other session.
   The description saves normally. Change that description through MCP instead:
   local text remains and Conflict offers Reload fields or Save my fields.
6. Add and delete a comment. Force a file-write failure: local card text and
   comment composer remain available, and failure is visible. Restore access
   and retry. A failure/conflict prevents leaving the project or closing Pact;
   switching tabs within the project retains the editor for later resolution.
7. Assign a session, navigate to it, then remove it. The card still exists and
   shows the retained name as unavailable; Open session is disabled.
8. Move a card to Verification, then Done, archive and restore it. Archived
   cards are read-only and absent from active lists. Restore appends to Done.
9. Switch Board → terminal → browser → Board. The terminal remains alive and
   the board retains its selected card. Restart Pact with Board selected:
   the tab and cards restore without starting a terminal solely for Board.
10. Change the project root through Settings. Pending text must save first.
    The new root opens its own board; the old JSON remains under Settings/Boards.
    Switch back and verify the original cards.
11. Open a card without a description and click **Delete**. Its drawer closes
    and the card disappears immediately. Add a description to another card and
    click **Delete**. Dismiss the confirmation and verify the card stays. Repeat
    and confirm that the card and its comments disappear.
12. Archive two Done cards and leave a third card active. Open Archive and
    select **Clean archive**. Decline once, then confirm. The archive empties,
    its cleanup button becomes disabled, and the active card remains after a
    restart.

Record actual outcomes separately. A successful native WebView gate verifies
terminal/browser integration, not these manual Board interactions.
