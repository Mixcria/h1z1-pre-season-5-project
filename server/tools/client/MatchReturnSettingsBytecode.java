import java.io.*;
import java.nio.file.*;
import java.util.*;
import com.jpexs.decompiler.flash.SWF;
import com.jpexs.decompiler.flash.abc.types.MethodBody;

// Preserve the private settings helper classes; replace only the close-menu call.
public class MatchReturnSettingsBytecode extends BinocularHudBytecode {
    public static void main(String[] args) throws Exception {
        SWF swf;
        try (InputStream in = Files.newInputStream(Path.of(args[0]))) { swf = new SWF(in, false); }
        require(swf.getAbcList().size() == 1, "Expected one ABC tag");
        abc = swf.getAbcList().get(0).getABC();
        Map<MethodBody, byte[]> originals = new IdentityHashMap<>();
        for (MethodBody body : abc.bodies) originals.put(body, body.getCodeBytes().clone());
        int stage = 0, event = 0, dispatch = 0;
        for (var instruction : method("UISettingsManager", "handleCloseSettings").getCode().code) {
            int code = instruction.definition.instructionCode;
            if (code == 0x66 && name(instruction).equals("_stage")) stage = instruction.operands[0];
            if ((code == 0x5d || code == 0x60) && name(instruction).equals("GameEvent")) event = instruction.operands[0];
            if (code == 0x4f && name(instruction).equals("dispatchEvent")) dispatch = instruction.operands[0];
        }
        require(stage != 0 && event != 0 && dispatch != 0, "Expected settings event bindings");
        MethodBody changed = method("UISettingsManager", "handleLogoutConfirmationResponse");
        int sites = 0;
        for (int i = changed.getCode().code.size() - 1; i >= 0; i--) {
            var instruction = changed.getCode().code.get(i);
            if (instruction.definition.instructionCode == 0x4f && name(instruction).equals("handleCloseSettings")) {
                require(instruction.operands[1] == 0, "Unexpected close arguments");
                instruction.operands[0] = dispatch;
                instruction.operands[1] = 1;
                changed.insertAll(i, List.of(op(0x66, stage), op(0x60, event),
                    op(0x2c, abc.constants.getStringId("CranberryCloseMatchMenu", true)), op(0x42, 1)));
                sites++;
            }
        }
        require(sites == 1, "Expected current cancellable match-exit confirmation");
        changed.max_stack += 2;
        changed.setModified();
        for (MethodBody body : abc.bodies) if (body != changed)
            require(Arrays.equals(originals.get(body), body.getCodeBytes()), "Unrelated method changed");
        ((com.jpexs.decompiler.flash.tags.Tag)swf.getAbcList().get(0)).setModified(true);
        try (OutputStream out = Files.newOutputStream(Path.of(args[1]))) { swf.saveTo(out); }
        System.out.println("Preserved " + (abc.bodies.size()-1) + " unrelated method bodies");
    }
}
