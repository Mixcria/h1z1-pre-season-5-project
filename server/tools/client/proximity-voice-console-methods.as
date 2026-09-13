      private function deliverCompanionVoice(message:String) : void
      {
         var status:String = "route:missing";
         var ids:Array = [WidgetNames.HUD_GROUP_VOICE_WINDOW,WidgetNames.HUD_GROUP_VOICE_WINDOW_TS];
         for(var i:int = 0; i < ids.length; i++)
         {
            var voiceWidget:Object = MainController.getWidgetManager().getLoadedWidget(uint(ids[i]));
            if(voiceWidget == null || !("receiveCompanionVoice" in voiceWidget)) continue;
            try
            {
               voiceWidget.receiveCompanionVoice(message);
               status = "route:ready";
            }
            catch(error:Error)
            {
               status = "route:error:" + error.errorID;
            }
         }
         if(status != this.m_voiceRouteStatus)
         {
            this.m_voiceRouteStatus = status;
            UIBindingSystem.DispatchWallOfData("CRANBERRY_VOICE_HUD_V1",status);
         }
      }
