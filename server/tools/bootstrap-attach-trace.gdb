# Attach-mode zone-bootstrap trace. The August client crashes at 0x28097d4f4 within two
# seconds when it is *started* by gdb, so start it normally (run-client.ps1 -NoKey), attach with
#
#   gdb -batch -p <pid> -x C:\Aug2017\Server\tools\bootstrap-attach-trace.gdb > logs\gdb-bootstrap-<stamp>.out.log 2>&1
#
# then post the splash / title keys with press-client-key.ps1. Breakpoints: the SendSelfToClient
# outer parse and player creation (FUN_140af3950 case 3), the loader's return (no abort),
# PostInitialize's world init (FUN_140b36780 -> FUN_1423c7610), the heightfield world Init,
# zone asset opens and the local-actor creator (FUN_140acccd0). An access violation stops the
# run and prints the registers and a backtrace.
set pagination off
set confirm off
set breakpoint pending on
set print thread-events off
handle SIGILL nostop noprint pass
# The client uses handled access violations during startup (first-chance SIGSEGV with
# 0xfefefefe fill patterns seconds after attaching); pass them through and catch failures at
# their entry points instead.
handle SIGSEGV nostop noprint pass

# FUN_1409e1080: the self-record loader's exact-consumption assert (*(int*)0 = 0xbadbeef)
break *0x1409e1080
commands
  printf "TRACE ASSERT_BADBEEF\n"
  bt 12
  continue
end

# FUN_140b882b0(client, 1, 0x16, message, 0): fatal error -> shutdown ("... Exiting.")
break *0x140b882b0
commands
  printf "TRACE CLIENT_FATAL rdx=%d r8=%d msg=", $rdx, $r8d
  x/s $r9
  bt 10
  continue
end

# FUN_140b33f60: "Failed to load local player actor"
break *0x140b33f60
commands
  printf "TRACE ACTOR_LOAD_FAILED\n"
  bt 10
  continue
end

# FUN_141596c90(obj): lock/guard helper (FUN_140e427e0 checksum guard); in the 20:15 run it was
# called with obj = 0xdd during actor creation and the guard faulted. Catch bogus object pointers
# with the return address still on the stack so the caller can be identified.
break *0x141596c90 if $rcx < 0x100000
commands
  printf "TRACE GUARD_BAD_OBJECT rcx=%p rdx=%p r8=%p\n", $rcx, $rdx, $r8
  x/4gx $rsp
  bt 12
  continue
end

# FUN_140b36780: PostInitialize
break *0x140b36780
commands
  silent
  printf "TRACE POST_INITIALIZE\n"
  continue
end

break *0x140af4223
commands
  silent
  printf "TRACE SELF_PARSE_RETURN al=%u\n", $eax & 0xff
  continue
end

break *0x140af4286
commands
  silent
  printf "TRACE SELF_CREATED player=%p\n", $r15
  # From here on an access violation is worth a stop: the startup-phase ones are behind us.
  handle SIGSEGV stop print
  continue
end

break *0x140af4348
commands
  silent
  printf "TRACE SELF_LOADER_RETURNED\n"
  continue
end

break *0x140b3684d
commands
  silent
  printf "TRACE WORLD_CALL type=%d name=", $r8d
  x/s $rdx
  continue
end

break *0x140b36852
commands
  silent
  printf "TRACE WORLD_RETURN al=%u storedType=%d name=", $eax & 0xff, *(int *)($rdi+0x32298)
  x/s *(void **)($rdi+0x32288)
  continue
end

break *0x142220f80
commands
  silent
  printf "TRACE HEIGHTFIELD_INIT name="
  x/s $rdx
  continue
end

break *0x142221020
commands
  silent
  printf "TRACE HEIGHTFIELD_BASE_RETURN al=%u\n", $eax & 0xff
  continue
end

break *0x142d4f240
commands
  silent
  printf "TRACE ZONE_ASSET_OPEN name="
  x/s *(void **)($rcx+0x28)
  continue
end

break *0x142d4f2b5
commands
  silent
  printf "TRACE ZONE_ASSET_RESULT raw=%d\n", $eax
  continue
end

break *0x140acccd0
commands
  silent
  printf "TRACE ACTOR_CREATE rcx=%p rdx=%p\n", $rcx, $rdx
  continue
end

continue
printf "TRACE STOP1 pc=%p\n", $pc
info registers rip rsp rax rbx rcx rdx rsi rdi r8 r9 r10 r11 r12 r13 r14 r15
x/12i $pc-24
bt 25
# A second continue tells a handled first-chance exception from a fatal one.
continue
printf "TRACE STOP2 pc=%p\n", $pc
info registers rip rax rcx rdx
bt 12
