#!/usr/bin/env python3
"""Build the August news-panel removal from the currently installed UI assets.

Uses the existing compiler/re-export checks, preserving unrelated UI methods,
GFx tags and purchase-success messages. Does not install client files.
"""
import importlib.util
from pathlib import Path


spec = importlib.util.spec_from_file_location(
    "news_build_tools", Path(__file__).with_name("build-menu-polish.py"))
menu = importlib.util.module_from_spec(spec)
spec.loader.exec_module(menu)


def hide_news(source):
    start, end = menu.emote.method_span(source, "refreshViewState")
    method = source[start:end]
    import re
    method, count = re.subn(r"this\._container\.visible = [^;]+;",
                           "this._container.visible = false;", method)
    if count != 1:
        raise ValueError("Expected one news-container visibility assignment")
    method = menu.emote.replace_once(method,
        "this._bbpanels.paused = this._panelHidden || _purchaseSuccess;",
        "this._bbpanels.paused = true;")
    return source[:start] + method + source[end:]


def hide_timer(source):
    if "this.timerLabel.visible = false;" in source:
        return source
    return menu.patch_news_widget(source)


menu.PATCHES = {
    "UIRoot.gfx": {menu.CONTROLLER: (hide_news, ("refreshViewState",)),
                   menu.WIDGET: (hide_timer, ("initialize",))},
    "CharacterSelectWindow.gfx": {menu.CONTROLLER: (hide_news, ("refreshViewState",)),
                                 menu.WIDGET: (hide_timer, ("initialize",))},
}

# FFDec 26 shortens the existing SQL string accumulation when recompiling the
# controller. Normalize only that known equivalent expression for comparison.
original_unrelated = menu.unrelated


def unrelated(source, methods):
    source = source.replace('_loc5_ = _loc5_ + ', '_loc5_ += ')
    return original_unrelated(source, methods)


menu.unrelated = unrelated


if __name__ == "__main__":
    menu.main()
