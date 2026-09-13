      private function canPlayAgain() : Boolean
      {
         return gameType == GAMETYPE_SOLO && !UIBindingSystem.IsTeamBattleRoyale()
            && !UIBindingSystem.IsTraining() && !UIBindingSystem.InInvitational();
      }

      private function installResultButtons(button:Button) : void
      {
         // Binding can precede stage attachment and the panel's timeline layout.
         button.addEventListener(Event.ENTER_FRAME,this.layoutResultButtons,false,0,true);
         button.addEventListener(Event.REMOVED_FROM_STAGE,this.removeResultButtons,false,0,true);
      }

      private function layoutResultButtons(event:Event) : void
      {
         var button:Button = event.currentTarget as Button;
         if(!button || !button.parent || !button.stage || button.width <= 0 || button.height <= 0) return;
         var solo:Boolean = this.canPlayAgain();
         var width:Number = button.width;
         var height:Number = button.height;
         var key:String = String(solo) + ":" + width + ":" + height;
         var row:Sprite = this.m_resultRows[button];
         if(row && (row.name != key || row.parent != button.parent))
         {
            if(row.parent) row.parent.removeChild(row);
            delete this.m_resultRows[button];
            row = null;
         }
         if(!row)
         {
            row = new Sprite();
            row.name = key;
            var gap:Number = Math.min(12,width * 0.05);
            var actionWidth:Number = solo ? (width - gap) / 2 : width;
            if(solo) row.addChild(this.makeResultButton("PLAY AGAIN","play",actionWidth,height,0xB51F29));
            var menu:Sprite = this.makeResultButton("MAIN MENU","menu",actionWidth,height,0x252525);
            menu.x = solo ? actionWidth + gap : 0;
            row.addChild(menu);
            button.parent.addChild(row);
            this.m_resultRows[button] = row;
         }
         row.x = button.x;
         row.y = button.y;
         row.alpha = button.alpha;
         row.visible = !this.m_nativeExitStarted;
         // Timeline frame scripts can restore these after initial binding.
         button.visible = false;
         button.mouseEnabled = false;
         button.focusable = false;
         button.tabEnabled = false;
      }

      private function removeResultButtons(event:Event) : void
      {
         this.detachResultButton(event.currentTarget as Button);
      }

      private function detachResultButton(button:Button) : void
      {
         if(!button) return;
         button.removeEventListener(Event.ENTER_FRAME,this.layoutResultButtons);
         button.removeEventListener(Event.REMOVED_FROM_STAGE,this.removeResultButtons);
         var row:Sprite = this.m_resultRows[button];
         if(row && row.parent) row.parent.removeChild(row);
         delete this.m_resultRows[button];
         button.visible = true;
         button.mouseEnabled = true;
         button.focusable = true;
         button.tabEnabled = true;
      }

      private function clearResultButtons() : void
      {
         for(var button:Object in this.m_resultRows) this.detachResultButton(button as Button);
      }

      private function requestMatchAction(action:String) : void
      {
         if(this.m_exitRequested || action == "play" && !this.canPlayAgain()) return;
         this.m_exitRequested = true;
         UIBindingSystem.DispatchWallOfData("CRANBERRY_MATCH_ACTION_V1",action);
      }

      private function handleMatchSessionReady(event:GameEvent) : void
      {
         clearTimeout(this.m_nativeExitTimeoutId);
         this.m_nativeExitTimeoutId = 0;
         this.m_exitRequested = false;
         this.m_nativeExitStarted = false;
      }

      private function handleNativeMatchExit(event:GameEvent) : void
      {
         if(this.m_nativeExitStarted) return;
         this.m_nativeExitStarted = true;
         this.m_nativeExitTimeoutId = setTimeout(this.finishNativeMatchExit,0);
      }

      private function finishNativeMatchExit() : void
      {
         this.m_nativeExitTimeoutId = 0;
         if(!this.m_nativeExitStarted || !this._stage) return;
         UIBindingSound.PlayUiSound(UIBindingSound.UI_MATCH_END_PROGRESS_BAR_STOP);
         // Stop callbacks and close bound views before clearing the native data they read.
         this.removeMatchListeners();
         this.closeAllWindows();
         this.cleanUpMatchData();
         UIBindingSystem.Logout();
      }
