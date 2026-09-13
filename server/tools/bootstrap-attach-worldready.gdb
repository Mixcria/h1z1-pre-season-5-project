# Late attach for the WaitForFirstZone phase. Attach once the client has created its local
# actor (the host log shows its GameTimeSync request); attaching earlier trips the client's
# integrity guards. Prints the client's own readiness diagnostics that never reach its log file
# before the fatal exit:
#   FUN_140982af0(0, fmt, ...)  "WaitForWorldReady Status: ..." / "Zone not ready reason: %s" / "Pending Asset"
#   FUN_1409c4d00(0, msg)       "WaitForWorldReady: Zone did not load, aborting attempt" / "Stalled ..."
#   FUN_140b882b0(client, 1, code, msg, 0)  the fatal exit with its G-code
set pagination off
set confirm off
set breakpoint pending on
set print thread-events off
handle SIGILL nostop noprint pass
handle SIGSEGV nostop noprint pass

break *0x140982af0
commands
  silent
  printf "TRACE STATUS fmt="
  x/s $rdx
  printf "TRACE STATUS r8=%p r9=%p\n", $r8, $r9
  if $r8 > 0x140000000 && $r8 < 0x150000000
    printf "TRACE STATUS r8str="
    x/s $r8
  end
  continue
end

break *0x1409c4d00
commands
  silent
  printf "TRACE ABORTLOG msg="
  x/s $rdx
  continue
end

break *0x140b882b0
commands
  printf "TRACE CLIENT_FATAL rdx=%d code=%d msg=", $rdx, $r8d
  x/s $r9
  bt 8
  continue
end

continue
printf "TRACE STOP pc=%p\n", $pc
bt 10
