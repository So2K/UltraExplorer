"""Ask UltraExplorer to pick files or a folder.

    from pick import pick
    folder = pick(mode="folder", title="Куда импортировать")
    files  = pick(filter="Component|*.component;*.json|All Files|*.*", multiselect=True)
    target = pick(mode="save", file_name="export", ext="txt")

Returns a list of paths, or an empty list when the user cancelled.
"""

import json
import os
import subprocess
import tempfile
import uuid

EXECUTABLE = "UltraExplorer.exe"

ACCEPTED, CANCELLED, ERROR = 0, 1, 2


def pick(
    mode="open",
    title=None,
    filter=None,
    file_name=None,
    ext=None,
    start=None,
    ok_label=None,
    multiselect=False,
    show_hidden=False,
    client_guid=None,
    executable=EXECUTABLE,
):
    result_file = os.path.join(tempfile.gettempdir(), "ultrapick-%s.json" % uuid.uuid4().hex)

    argv = [executable, "--pick", "--mode", mode, "--result", result_file]
    for switch, value in (
        ("--title", title),
        ("--filter", filter),
        ("--file-name", file_name),
        ("--ext", ext),
        ("--start", start),
        ("--ok-label", ok_label),
        ("--client-guid", client_guid),
    ):
        if value:
            argv += [switch, value]
    if multiselect:
        argv.append("--multiselect")
    if show_hidden:
        argv += ["--flag", "ForceShowHidden"]

    try:
        # subprocess passes the list through without a shell, so a filter full
        # of semicolons and parentheses arrives intact.
        code = subprocess.call(argv)
        if code != ACCEPTED or not os.path.exists(result_file):
            return []

        with open(result_file, "r", encoding="utf-8") as handle:
            answer = json.load(handle)

        return answer.get("paths", []) if answer.get("accepted") else []
    finally:
        if os.path.exists(result_file):
            os.remove(result_file)


if __name__ == "__main__":
    print(pick(mode="folder", title="Pick a folder"))
