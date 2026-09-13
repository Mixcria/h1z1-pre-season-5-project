      private function clearCompanionVoice() : void
      {
         for(var i:int = 0; i < this.m_voiceMembers.length; i++)
         {
            this.m_voiceMembers[i].setCompanion("");
         }
         this.m_voiceReceivedAt = -10000;
         this.layoutCompanionVoice(0);
         this.reportCompanionVoice(0);
      }

      public function receiveCompanionVoice(message:String) : void
      {
         if(message == null || message.indexOf("@cranberry/voice/1;") != 0 || message.length > 8192) return;
         var parts:Array = message.split(";");
         var names:Array = [];
         var seen:Object = {};
         for(var i:int = 1; i < parts.length && names.length < this.m_voiceMembers.length; i++)
         {
            var fields:Array = String(parts[i]).split("|");
            if(fields.length != 2 || !this.isCompanionVoiceId(String(fields[0])) || seen[fields[0]]) continue;
            var name:String = "";
            try { name = decodeURIComponent(String(fields[1])); }
            catch(error:Error) { continue; }
            if(name.length == 0 || name.length > 64) continue;
            seen[fields[0]] = true;
            names.push(name);
         }
         for(var row:int = 0; row < this.m_voiceMembers.length; row++)
         {
            this.m_voiceMembers[row].setCompanion(row < names.length ? String(names[row]) : "");
         }
         this.m_voiceReceivedAt = getTimer();
         this.layoutCompanionVoice(names.length);
         this.reportCompanionVoice(names.length);
      }

      private function layoutCompanionVoice(count:int) : void
      {
         // Keep the authored spacing and end the proximity list above the team HUD.
         // One speaker sits around the middle-left; more speakers extend upward.
         var spacing:Number = this.m_voiceMembers[1].y - this.m_voiceMembers[0].y;
         if(spacing <= 0 || spacing > 100) spacing = 26;
         var top:Number = -32 - spacing * Math.max(0,count - 1);
         for(var i:int = 0; i < this.m_voiceMembers.length; i++)
         {
            this.m_voiceMembers[i].y = top + spacing * i;
         }
      }

      private function isCompanionVoiceId(value:String) : Boolean
      {
         // The August Scaleform build does not provide a working RegExp.test.
         // Keep 64-bit identities as text and validate with supported String methods.
         if(value == null || value.length == 0 || value.length > 20) return false;
         for(var i:int = 0; i < value.length; i++)
         {
            var digit:Number = value.charCodeAt(i);
            if(digit < 48 || digit > 57) return false;
         }
         return true;
      }

      private function reportCompanionVoice(count:int) : void
      {
         var first:VoiceItemRenderer = this.m_voiceMembers[0];
         var display:DisplayObject = first;
         var shown:Boolean = true;
         while(display != null)
         {
            if(!display.visible || display.alpha == 0) shown = false;
            display = display.parent;
         }
         var point:Point = first.localToGlobal(new Point());
         var state:String = "render:" + count + ":" + (first.m_playerName ? first.m_playerName.text.length : -1)
            + ":" + (first.visible ? 1 : 0) + ":" + (shown ? 1 : 0)
            + ":" + (first.m_voiceIcon ? first.m_voiceIcon.currentFrame : -1)
            + ":" + int(point.x) + ":" + int(point.y);
         if(state != this.m_voiceReport)
         {
            this.m_voiceReport = state;
            UIBindingSystem.DispatchWallOfData("CRANBERRY_VOICE_HUD_V1",state);
         }
      }

      private function expireCompanionVoice(param1:TimerEvent) : void
      {
         if(this.m_voiceReceivedAt != -10000 && getTimer() - this.m_voiceReceivedAt > 1250)
         {
            this.clearCompanionVoice();
         }
      }
