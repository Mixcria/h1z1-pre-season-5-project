#!/usr/bin/env python3
"""Add Shred to Fast/Sturdy footwear in both installed August inventory menu copies.

Build only; Fast/Sturdy use the existing authenticated inventory WindowEvent binding.
Other actions, including Stealth footwear, retain the native RequestUseItem path.
The inputs are the September 7 installed menus. Non-script GFx tags remain byte-identical.
"""
import importlib.util
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
SOURCES = {
    'InventoryWindow.gfx': '59b8c69fd2cd3be8a1a67160f82789b199046fefe6c8fafd1fdd9bdc2c689759',
    'Component_Library.gfx': 'f2c50bec4db9879171528a6c0164f204637c25e5e0d78429e5b1b3d73edae269',
}
spec = importlib.util.spec_from_file_location('inventory_builder', Path(__file__).with_name('build-inventory-skins.py'))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


def transform(source, catalog):
    tiers = (ROOT / 'src/Cranberry.Zone/Movement/FootwearItems.g.cs').read_text(encoding='utf-8')
    items = {item: True for item in re.findall(r'\[(\d+)\] = FootwearTier\.(?:Fast|Sturdy)', tiers)}
    assert '2209' in items and '2215' in items and '3711' not in items
    field = '      private var m_skinCategory:int = 0;'
    draw = '         this.showActions(actions,true);'
    if source.count(field) != 1 or source.count(draw) != 1 or 'addFootwearShred' in source:
        raise ValueError('Unrecognized menu; preserve the installed UI changes')
    source = source.replace(field,
        '      private static const SHREDDABLE_FOOTWEAR:Object = '
        + json.dumps(items, separators=(',', ':')) + ';\n\n' + field, 1)
    source = source.replace(draw, '         this.addFootwearShred(actions);\n' + draw, 1)
    dispatch = '         ActionManager.getInstance().doAction(this.m_item,action);'
    if source.count(dispatch) != 1:
        raise ValueError('Unrecognized native action dispatch')
    source = source.replace(dispatch, '''         if((action.id == 6 || action.id == 63)
            && SHREDDABLE_FOOTWEAR[String(this.m_item.itemDefinitionId)])
         {
            UIBindingSystem.DispatchWallOfData("Cranberry.Inventory","shred:" + this.m_item.guid + ":1");
            this.setItem(null);
            return;
         }
''' + dispatch, 1)
    helper = '''
      private function addFootwearShred(actions:Array) : void
      {
         if(this.m_item == null || !SHREDDABLE_FOOTWEAR[String(this.m_item.itemDefinitionId)])
         {
            return;
         }
         for each(var action:ItemActionData in actions)
         {
            if(action.id == 6 || action.id == 63)
            {
               return;
            }
         }
         actions.push(new ItemActionData({Id:63,Name:"Shred",IconId:131,RequiresQuantity:1}));
      }

'''
    end = source.rfind('   }')
    if end < 0:
        raise ValueError('Missing class end')
    return source[:end] + helper + source[end:]


if __name__ == '__main__':
    name = Path(sys.argv[1]).name if len(sys.argv) > 1 else ''
    if name not in SOURCES:
        raise SystemExit('Pass InventoryWindow.gfx or Component_Library.gfx, --ffdec and --out')
    builder.SOURCE_SHA = SOURCES[name]
    builder.ASSET_NAME = name
    builder.transform = transform
    builder.main()
    output = Path(sys.argv[sys.argv.index('--out') + 1])
    verified = (output / 'verify/scripts/ui/controls/ItemActionMenu.as').read_text(encoding='utf-8')
    for marker in ('SHREDDABLE_FOOTWEAR', 'this.addFootwearShred(actions)', '"Shred"',
                   'action.id == 6 || action.id == 63', 'equippedSkinCategory', 'hoodieContext',
                   '"shred:" + this.m_item.guid + ":1"',
                   'ActionManager.getInstance().doAction(this.m_item,action)'):
        if marker not in verified:
            raise ValueError('Compiled menu is missing ' + marker)
    print('Footwear action and existing skin/hood dispatch verified after decompilation.')
