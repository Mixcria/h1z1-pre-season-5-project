#!/usr/bin/env python3
"""Update the two installed footwear menus to skin carried apparel using server eligibility."""
import importlib.util
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('builder', Path(__file__).with_name('build-inventory-skins.py'))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)
SOURCES = {
    'InventoryWindow.gfx': 'aff92e77a35cb44a3fff520ce4e98528b9bf0a19f69a56b66d4ca9a9859f1ced',
    'Component_Library.gfx': '32e50de5291d347fa071d06cc0a6f2581e7150cd39847e010f53415dd95dbf60',
}

def transform(source, catalog):
    return builder.method(source, 'equippedSkinCategory', '''
      private function equippedSkinCategory(item:IItemData) : int
      {
         if(item == null) { return 0; }
         // The authenticated server validates ownership, location and the saved skin.
         // A second loadout query loses cargo helmets and some worn backpack rows.
         var targets:String = ";" + String(UIBindingSystem.GetStringHashValue("Cranberry.Inventory.SkinTargets","")) + ";";
         if(targets.indexOf(";" + item.guid + ";") < 0) { return 0; }
         var category:int = int(SKIN_CATEGORY_BY_ITEM[String(item.itemDefinitionId)]);
         return category > 0 ? category : 1;
      }
''')

if __name__ == '__main__':
    name = Path(sys.argv[1]).name if len(sys.argv) > 1 else ''
    if name not in SOURCES: raise SystemExit('Pass InventoryWindow.gfx or Component_Library.gfx, --ffdec and --out')
    builder.SOURCE_SHA = SOURCES[name]
    builder.ASSET_NAME = name
    builder.transform = transform
    builder.main()
