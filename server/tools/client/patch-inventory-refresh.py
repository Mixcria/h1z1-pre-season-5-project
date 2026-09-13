#!/usr/bin/env python3
"""Stage the August UIInventoryManager bag-list refresh fix for a combined UI build.

Input must be the manager exported from the installed UIRoot. This changes one
class only; it does not build or install an asset. Item/proximity mutation rights
and the inventory movement policy are unaffected.
"""
import argparse
import hashlib
from pathlib import Path


def patch(source: str) -> str:
    def replace(old: str, new: str) -> None:
        nonlocal source
        if source.count(old) != 1:
            raise ValueError(f'Expected exactly one original fragment: {old[:90]}')
        source = source.replace(old, new)

    replace('import flash.events.TimerEvent;', 'import flash.events.Event;')
    replace('   import flash.utils.Timer;\n', '')
    replace('private var m_spamTimer:Timer;', 'private var m_inspectedUpdatePending:Boolean = false;')
    replace('      private var m_spamTimerCompleted:Boolean;\n', '')
    replace('''         this.m_spamTimer = new Timer(100,1);
         this.m_spamTimer.addEventListener(TimerEvent.TIMER_COMPLETE,this.onSpamTimerComplete,false,0,true);
         this.m_spamTimerCompleted = false;''', '         this.m_inspectedUpdatePending = false;')
    replace('this.m_spamTimer.removeEventListener(TimerEvent.TIMER_COMPLETE,this.onSpamTimerComplete);',
            'this.cancelInspectedUpdate();')
    replace('''      private function onSpamTimerComplete(param1:TimerEvent) : void
      {
         this.m_spamTimerCompleted = true;
         this.updateAccessedCharacterInventoryDb();
      }''', '''      private function cancelInspectedUpdate() : void
      {
         this.m_stage.removeEventListener(Event.ENTER_FRAME,this.onInspectedUpdateFrame);
         this.m_inspectedUpdatePending = false;
      }

      private function onInspectedUpdateFrame(param1:Event) : void
      {
         this.cancelInspectedUpdate();
         this.updateAccessedCharacterInventoryDb();
      }''')
    replace('''            case WidgetNames.INVENTORY_WINDOW:
               this.m_stage.dispatchEvent(new LegendEvent(LegendEvent.HIDE));''', '''            case WidgetNames.INVENTORY_WINDOW:
               this.cancelInspectedUpdate();
               this.m_stage.dispatchEvent(new LegendEvent(LegendEvent.HIDE));''')
    replace('''            if(this.m_spamTimerCompleted || param1 == null)
            {
               this.m_spamTimerCompleted = false;
               this.m_spamTimer.stop();''', '''            if(param1 == null)
            {
               this.cancelInspectedUpdate();''')
    replace('''               this.m_spamTimer.reset();
               this.m_spamTimer.start();''', '''               if(!this.m_inspectedUpdatePending)
               {
                  this.m_inspectedUpdatePending = true;
                  this.m_stage.addEventListener(Event.ENTER_FRAME,this.onInspectedUpdateFrame,false,0,true);
               }''')
    replace('''         if(this.m_isInProximity)
         {
            DropManager.getInstance().containerData = null;''', '''         if(this.m_isInProximity)
         {
            this.cancelInspectedUpdate();
            DropManager.getInstance().containerData = null;''')
    if 'm_spamTimer' in source:
        raise ValueError('Incomplete timer removal')
    return source


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    result = patch(args.source.read_text(encoding='utf-8'))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(result, encoding='utf-8', newline='\n')
    print(f'{args.output}: SHA256 {hashlib.sha256(args.output.read_bytes()).hexdigest()}')


if __name__ == '__main__':
    main()
