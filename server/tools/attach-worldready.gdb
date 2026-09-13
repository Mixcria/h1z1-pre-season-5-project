# WaitForFirstZone trace (attach mode). Attach only after the local actor exists — the client's
# ClientInitializationDetails / SetLocale packets prove PostInitialize ran, and actor creation is
# over within milliseconds of it; attaching during actor creation trips the integrity guard
# (FUN_140e427e0). Driven by C:\Aug2017\trace-worldready.ps1:
#
#   gdb -batch -p <pid> -x C:\Aug2017\Server\tools\attach-worldready.gdb
#
# Every address below is a function entry (verified FUN_ starts), so no int3 lands mid-instruction.
set pagination off
set confirm off
set breakpoint pending on
set print thread-events off
handle SIGILL nostop noprint pass
# Startup-phase access violations are behind us at this point; a SIGSEGV now is worth a stop.
handle SIGSEGV stop print pass

# Snapshot: what every thread is doing at attach time (the main thread should be inside the
# WaitForFirstZone evaluator; a blocked main thread shows up here).
printf "TRACE SNAPSHOT_BEGIN\n"
info threads
thread apply all bt 12
printf "TRACE SNAPSHOT_END\n"

# The client's fatal path spawns wws_crashreport_uploader.exe and blocks on it (WER: AppHangXProcB1
# "Waiting on Application Name wws_crashreport_uploader.exe"). Catch the spawn with its command line
# (x/hs = UTF-16) and the backtrace that led there.
break CreateProcessW
commands
  printf "TRACE CREATE_PROCESS_W app=%p cmd=", $rcx
  x/hs $rdx
  bt 24
  continue
end

break CreateProcessA
commands
  printf "TRACE CREATE_PROCESS_A cmd="
  x/s $rdx
  bt 24
  continue
end

# C++ throws (0xe06d7363) and custom raises go through here; gdb does not stop on them by itself.
break RaiseException
commands
  printf "TRACE RAISE_EXCEPTION code=0x%x flags=%d\n", $ecx, $edx
  bt 16
  continue
end

break TerminateProcess
commands
  printf "TRACE TERMINATE_PROCESS handle=%p code=%d\n", $rcx, $rdx
  bt 12
  continue
end

break NtTerminateProcess
commands
  printf "TRACE NT_TERMINATE_PROCESS handle=%p code=%d\n", $rcx, $rdx
  bt 12
  continue
end

break UnhandledExceptionFilter
commands
  printf "TRACE UNHANDLED_EXCEPTION_FILTER code=0x%x addr=%p\n", *(unsigned int *)(*(long long *)$rcx), *(long long *)(*(long long *)$rcx + 0x10)
  bt 16
  continue
end

# FUN_140982af0(0, fmt, ...): the evaluator's diagnostics (FUN_140b8e3d0), never flushed to a
# log file before the exit:
#   "WaitForWorldReady Status: IsZoneReady=%d HaveProxiedCharacter=%d HaveProxiedActor=%d
#    ActorReadyToDraw=%d WeatherDataSynced=%d, x=%f, y=%f, z=%f"   (r8, r9, stack 0x28/0x30/0x38, doubles 0x40..)
#   "WaitForWorldReady Status: Zone not ready reason: %s "        (r8 = string)
#   "Pending Asset: \"%s\" "                                        (r8 = string)
#   "WaitForWorldReady Actor Debug: %s"                             (r8 = string)
break *0x140982af0
commands
  silent
  printf "TRACE STATUS fmt="
  x/s $rdx
  printf "TRACE STATUS r8=%p r9=%p s28=%p s30=%p s38=%p\n", $r8, $r9, *(long long *)($rsp+0x28), *(long long *)($rsp+0x30), *(long long *)($rsp+0x38)
  printf "TRACE STATUS f40=%f f48=%f f50=%f\n", *(double *)($rsp+0x40), *(double *)($rsp+0x48), *(double *)($rsp+0x50)
  if $r8 > 0x10000
    printf "TRACE STATUS r8str="
    x/s $r8
  end
  continue
end

# FUN_1409c4d00(0, msg): "WaitForWorldReady: Zone did not load, aborting attempt",
# "WaitForWorldReady: Zone load failed, aborting attempt", "Stalled ...", "Disconnect from gateway ..."
break *0x1409c4d00
commands
  silent
  printf "TRACE ABORTLOG msg="
  x/s $rdx
  bt 6
  continue
end

# FUN_140b882b0(client, 1, code, msg, 0): the fatal exit with its G-code
break *0x140b882b0
commands
  printf "TRACE CLIENT_FATAL rdx=%d code=%d msg=", $rdx, $r8d
  x/s $r9
  bt 8
  continue
end

# FUN_1423c7220(worldMgr): world unload (clears the loaded flag +0x4c)
break *0x1423c7220
commands
  printf "TRACE WORLD_UNLOAD mgr=%p loaded=%d type=%d\n", $rcx, *(char *)($rcx+0x4c), *(int *)($rcx+0x48)
  bt 8
  continue
end

# FUN_140b89710(client, state): run-state setter (0x23 = startup failed, 0xa = the -1 exit)
break *0x140b89710
commands
  silent
  printf "TRACE RUN_STATE new=%d\n", $rdx
  bt 4
  continue
end

# FUN_1423c7750(worldMgr, &pos): the WaitForFirstZone entry hands the player position to the world
break *0x1423c7750
commands
  silent
  printf "TRACE WORLD_SET_POS mgr=%p loaded=%d hold=%d pos=", $rcx, *(char *)($rcx+0x4c), *(char *)($rcx+0x4f)
  x/4f $rdx
  continue
end

# FUN_140b864c0(client, zoning, loading): WaitForFirstZone entry
break *0x140b864c0
commands
  silent
  printf "TRACE WFWR_ENTER zoning=%d loading=%d\n", $rdx, $r8d
  continue
end

# FUN_1423c76b0(worldMgr): IsZoneReady — print the manager and implementation readiness state once per call
# (called every frame; keep silent and cheap: only print when the loaded flag is clear)
break *0x1423c76b0 if *(char *)($rcx+0x4c) == 0
commands
  silent
  printf "TRACE ISZONEREADY loaded=0 type=%d\n", *(int *)($rcx+0x48)
  bt 4
  continue
end

break ExitProcess
commands
  printf "TRACE EXIT_PROCESS code=%d\n", $rcx
  bt 12
  continue
end

continue
printf "TRACE STOP1 pc=%p\n", $pc
info registers rip rsp rax rbx rcx rdx rsi rdi r8 r9 r10 r11 r12 r13 r14 r15
x/12i $pc-24
bt 25
continue
printf "TRACE STOP2 pc=%p\n", $pc
info registers rip rax rcx rdx
bt 12
continue
printf "TRACE STOP3 pc=%p\n", $pc
bt 12
continue
printf "TRACE STOP4 pc=%p\n", $pc
bt 12
