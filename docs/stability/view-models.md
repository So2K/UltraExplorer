# View-model and folder-list stabilization

Review scope: MainViewModel startup, save/merge ordering and disposal; folder-list
read/navigation supersession; search callbacks and file-operation caller paths.
Search text-content routing was fixed separately in 7ba3877.

## Confirmed folder-list failures

An old provider that ignored cancellation could throw IOException or access
denied after a newer directory had completed. Its catch cleared the newer rows,
count and status. Even an old successful/cancelled load subsequently invoked
the old canvas activation from NavigateAsync. A controlled TaskCompletionSource
provider reproduced these failures: 4/9 before, 9/9 after.

Errors now update only their still-current load. A folder-version ticket prevents
overtaken navigation and sort rereads from reactivating or highlighting a newer
folder. The real folder-list/search regression run passed 140/140. All files and
provider results in the new test are owned temporary fixtures.

## Confirmed model lifetime and save ordering

A disposed MainViewModel could continue startup, add 14 sidebar entries, emit
two late notifications, respond to former Orders events and write workspace
after release. Real isolated model tests, including an 8 MB valid note to hold
asynchronous loading, reproduced 6/14 before. Disposal is now checked around
startup awaits and saves, and the Orders handler is detached. After: 303/303
requested model/preferences/settings/lifetime assertions.

SaveNowAsync previously captured UI state before an asynchronous workspace load,
then relied on the store's write-only semaphore. An older delayed continuation
could enqueue its old split/active-pane state last. Two held synchronization
contexts reproduced the race against the real model/store without sleeps,
reflection or product hooks (5/6 before). The whole snapshot/read/merge/write
sequence now takes one VM gate; queued calls capture current state after entry.
Native picker navigation preferences use the same gate without nested waiting.
Ordering, release and subsequent flush regression: 33/33 with tree/store tests.

## Limits and remaining coordination

No user media association, recycle-bin operation or file mutation was used for
qualification. Cross-process preference/guardian/caller-store findings are
assigned to Claude and recorded in coordination/messages. A green isolated
suite does not prove every hardware, cloud, SMB or provider failure path.
