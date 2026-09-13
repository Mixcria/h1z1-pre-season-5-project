      // Sent only after a pending load disappears or an overlay releases ownership.
      // WidgetManager intentionally suppresses CLOSE_WIDGET for a cancelled load.
      private function handleInteractionInputRefresh(param1:Event) : void
      {
         param1.preventDefault();
         var manager:WidgetManager = MainController.getWidgetManager();
         var ids:Array = [WidgetNames.INVENTORY_WINDOW,WidgetNames.TAB_NAVIGATION_WINDOW,
            WidgetNames.CONFIRMATION_DIALOG_WINDOW,WidgetNames.INGAME_BROWSER_WINDOW,
            WidgetNames.MAP_WINDOW,WidgetNames.CONSOLE_WINDOW];
         for(var index:int = 0; index < ids.length; index++)
         {
            var id:uint = uint(ids[index]);
            this.m_movementWindows[id] = manager.isWidgetLoaded(id) || manager.isWidgetLoading(id);
         }
         if(!UISettingsManager.HAS_KEY_TRAPPED_FOR_KEYBINDING && !UIBindingPlayer.IsKnockedOut() && !this.m_matchOver)
         {
            if(param1 is flash.events.DataEvent)
            {
               // Preserve capture already owned before the overlay, and unrelated current bits.
               var savedFlags:uint = uint(Object(param1).data);
               UIBindingInput.SetInputFlags(UIBindingInput.GetInputFlags() &
                  ~(~savedFlags & (UIBindingInput.INPUT_MOUSE | UIBindingInput.INPUT_KEYBOARD)));
            }
            var padCaptured:Boolean = Boolean(this.m_movementWindows[WidgetNames.TAB_NAVIGATION_WINDOW]) ||
               Boolean(this.m_movementWindows[WidgetNames.CONFIRMATION_DIALOG_WINDOW]);
            if(padCaptured)
            {
               UIBindingInput.SetInputFlags(UIBindingInput.GetInputFlags() | UIBindingInput.INPUT_GAMEPAD_ALL);
            }
            else
            {
               UIBindingInput.SetInputFlags(UIBindingInput.GetInputFlags() & ~UIBindingInput.INPUT_GAMEPAD_ALL);
            }
         }
         this.handleSetMouse();
         this.applyInventoryMovement();
         this.traceInteractionInput("reconcile");
      }

      private function applyInventoryMovement() : void
      {
         // Preserve native death/knockout and keybinding ownership during recovery.
         if(UISettingsManager.HAS_KEY_TRAPPED_FOR_KEYBINDING || UIBindingPlayer.IsKnockedOut() || this.m_matchOver)
         {
            return;
         }
         var blocked:Boolean = UIConsoleManager.OverlayActive() ||
            Boolean(this.m_movementWindows[WidgetNames.CONFIRMATION_DIALOG_WINDOW]) ||
            Boolean(this.m_movementWindows[WidgetNames.INGAME_BROWSER_WINDOW]) ||
            Boolean(this.m_movementWindows[WidgetNames.MAP_WINDOW]) ||
            Boolean(this.m_movementWindows[WidgetNames.CONSOLE_WINDOW]);
         var tabOpen:Boolean = Boolean(this.m_movementWindows[WidgetNames.TAB_NAVIGATION_WINDOW]);
         var inventoryOpen:Boolean = Boolean(this.m_movementWindows[WidgetNames.INVENTORY_WINDOW]);
         UIBindingKeyboard.SetMovementEnabled(!blocked && (!tabOpen || inventoryOpen));
         UIBindingKeyboard.SetKeyboardEnabled(!blocked && (!tabOpen || inventoryOpen));
      }

      private function traceInteractionInput(reason:String) : void
      {
         if(this.m_interactionInputTraceCount >= 128 || UIBindingSystem.DispatchWallOfData == null)
         {
            return;
         }
         var state:String = "flags=" + UIBindingInput.GetInputFlags() +
            " hidden=" + UIBindingSystem.GetMouseHidden() +
            " keyboard=" + UIBindingKeyboard.GetKeyboardEnabled() +
            " movement=" + UIBindingKeyboard.GetMovementEnabled() +
            " inventory=" + Boolean(this.m_movementWindows[WidgetNames.INVENTORY_WINDOW]) +
            " tab=" + Boolean(this.m_movementWindows[WidgetNames.TAB_NAVIGATION_WINDOW]) +
            " windows=" + this.m_displayStack.windowDepth + " modal=" + this.m_displayStack.modalOpen +
            " overlay=" + UIConsoleManager.OverlayActive();
         if(state == this.m_interactionInputTraceLast)
         {
            return;
         }
         this.m_interactionInputTraceLast = state;
         this.m_interactionInputTraceCount++;
         UIBindingSystem.DispatchWallOfData("InteractionInput",reason + " " + state);
      }
