"""Normalize console prefixes while retaining original August command handlers.

The original vehicle/item/goto parsers own their names and packet formats. Add the
slash for bare commands and accept ./ as a spelling of /. Keep command names,
case and arguments intact. Preserve the existing /help-to-/commands compatibility
alias and pass the original input to the local UI processor.
"""

ORIGINAL = 'UIBindingChat.ProcessChatCommand(param1.currentTarget.text);'
HELP_ONLY = (
    'UIBindingChat.ProcessChatCommand(String(param1.currentTarget.text).replace('
    r'/^(\s*)\/help(?=\s|$)/i,"$1/commands"));'
)
PATCHED = (
    'UIBindingChat.ProcessChatCommand(String(param1.currentTarget.text).replace('
    r'/^(\s*)(?:\.\/)?(?=[a-z_])/i,"$1/").replace(/^(\s*)\/help(?=\s|$)/i,"$1/commands"));'
)


def patch_source(source):
    if PATCHED in source:
        raise ValueError('Console command aliases are already patched')
    # Upgrade only the exact earlier help patch, never replace an unknown handler.
    if source.count(ORIGINAL) + source.count(HELP_ONLY) != 1:
        raise ValueError('Unexpected August console submit source')
    return source.replace(HELP_ONLY if HELP_ONLY in source else ORIGINAL, PATCHED, 1)
