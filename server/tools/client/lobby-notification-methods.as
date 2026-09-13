
      public static function LocalShowInvite(invite:String, inviter:String) : void
      {
         if(invite == "")
         {
            m_localInvite = "";
            return;
         }
         if(invite == m_localInvite)
         {
            LocalSend("seen " + invite);
            return;
         }
         if(invite != m_localAlertedInvite)
         {
            UIBindingSound.PlayUiSound(UIBindingSound.UI_INVITE_NOTIFICATION);
            LocalNotice(inviter + " invited you to their lobby. Accept the invitation to play Duos or Fives together.");
            m_localAlertedInvite = invite;
         }
         // Wait for an existing modal to close. The server retries unacknowledged
         // invitations; placing one behind an unrelated modal can outlive its token.
         if(MainController.getWidgetManager().isWidgetLoaded(WidgetNames.CONFIRMATION_DIALOG_WINDOW)
            || MainController.getWidgetManager().isWidgetLoading(WidgetNames.CONFIRMATION_DIALOG_WINDOW)) return;
         // Release chat's input capture before showing the native invitation buttons.
         UIConsoleManager.OverlayCloseForInvite();
         MainController.showSystemMessage({
            "message":UIBindingChat.MakeHtmlSafe(inviter) + " invited you to their lobby. Join to queue Duos or Fives together.",
            "accept":UIBindingLocale.translateCodeString("UI.Accept"),
            "decline":UIBindingLocale.translateCodeString("UI.Decline"),
            "timeout":60,
            "callback":function(accepted:Boolean):void
            {
               if(invite == m_localInvite)
                  LocalSend((accepted ? "accept " : "decline ") + invite);
            }
         });
         m_localInvite = invite;
         LocalSend("seen " + invite);
      }
