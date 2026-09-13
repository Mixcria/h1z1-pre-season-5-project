import java.io.*;
import java.nio.file.*;
import java.util.*;
import com.jpexs.decompiler.flash.SWF;

// Confirm an FFDec rendering artifact does not represent an actual method change.
public class VerifyMatchExitBytecode extends BinocularHudBytecode {
    public static void main(String[] args) throws Exception {
        SWF original, candidate;
        try (InputStream in = Files.newInputStream(Path.of(args[0]))) { original = new SWF(in, false); }
        try (InputStream in = Files.newInputStream(Path.of(args[1]))) { candidate = new SWF(in, false); }
        abc = original.getAbcList().get(0).getABC();
        var before = method(args[2], args[3]);
        abc = candidate.getAbcList().get(0).getABC();
        var after = method(args[2], args[3]);
        require(Arrays.equals(before.getCodeBytes(), after.getCodeBytes()), "Unrelated bytecode changed");
        require(before.max_stack == after.max_stack && before.max_regs == after.max_regs
            && before.init_scope_depth == after.init_scope_depth && before.max_scope_depth == after.max_scope_depth,
            "Unrelated method limits changed");
        System.out.println("Bytecode and frame unchanged: " + args[2] + "." + args[3]);
    }
}
