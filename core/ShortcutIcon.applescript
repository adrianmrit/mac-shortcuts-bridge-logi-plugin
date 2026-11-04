on run argv
    set shortcutIdentifier to item 1 of argv
    set destinationPath to item 2 of argv
    tell application "Shortcuts"
        set shortcutImage to icon of (first shortcut whose id is shortcutIdentifier)
    end tell
    set destinationFile to open for access (POSIX file destinationPath) with write permission
    try
        set eof destinationFile to 0
        write shortcutImage to destinationFile
        close access destinationFile
    on error errorMessage number errorNumber
        close access destinationFile
        error errorMessage number errorNumber
    end try
end run
