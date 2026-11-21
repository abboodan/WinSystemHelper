if ($result -eq [System.Windows.Forms.DialogResult]::Yes) {
    exit {{askYesExitCode}}
}

exit {{askNoExitCode}}
