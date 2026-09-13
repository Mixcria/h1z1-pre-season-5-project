import java.io.*;
import java.nio.file.*;
import java.util.*;
import com.jpexs.decompiler.flash.SWF;
import com.jpexs.decompiler.flash.abc.types.MethodBody;

// Only the confirmation method changes; retain the settings package's private helper classes.
public class MatchExitSettingsBytecode extends BinocularHudBytecode {
    public static void main(String[] args) throws Exception {
        SWF swf;
        try (InputStream in = Files.newInputStream(Path.of(args[0]))) { swf = new SWF(in, false); }
        require(swf.getAbcList().size() == 1, "Expected one ABC tag");
        abc = swf.getAbcList().get(0).getABC();
        Map<MethodBody, byte[]> originals = new IdentityHashMap<>();
        for (MethodBody body : abc.bodies) originals.put(body, body.getCodeBytes().clone());
        MethodBody changed = method("UISettingsManager", "handleLogoutConfirmationResponse");
        int close = 0;
        for (var instance : abc.instance_info) {
            if (!abc.constants.getString(abc.constants.getMultiname(instance.name_index).name_index).equals("UISettingsManager")) continue;
            for (var trait : instance.instance_traits.traits)
                if (abc.constants.getString(abc.constants.getMultiname(trait.name_index).name_index).equals("handleCloseSettings")) {
                    require(close == 0, "Duplicate settings close method");
                    close = trait.name_index;
                }
        }
        require(close != 0, "Settings close method missing");
        int dispatch = 0;
        for (MethodBody body : abc.bodies) for (var instruction : body.getCode().code)
            if ((instruction.definition.instructionCode == 0x4f || instruction.definition.instructionCode == 0x46)
                && name(instruction).equals("DispatchWallOfData")) dispatch = instruction.operands[0];
        require(dispatch != 0, "DispatchWallOfData binding missing");
        int sites = 0;
        for (int i = changed.getCode().code.size() - 1; i >= 0; i--) {
            var instruction = changed.getCode().code.get(i);
            if ((instruction.definition.instructionCode == 0x4f || instruction.definition.instructionCode == 0x46)
                && name(instruction).equals("Logout")) {
                require(instruction.operands[1] == 0, "Unexpected Logout arguments");
                require(i > 0 && changed.getCode().code.get(i - 1).definition.instructionCode == 0x60
                    && name(changed.getCode().code.get(i - 1)).equals("UIBindingSystem"), "Unexpected Logout receiver");
                instruction.operands[0] = dispatch;
                instruction.operands[1] = 2;
                changed.insertAll(i, List.of(op(0x2c, abc.constants.getStringId("CRANBERRY_MATCH_ACTION_V1", true)),
                    op(0x2c, abc.constants.getStringId("exit", true))));
                changed.insertAll(i - 1, List.of(op(0xd0), op(0x4f, close, 0)));
                sites++;
            }
        }
        require(sites == 1, "Expected one native logout call");
        changed.max_stack += 2;
        changed.setModified();
        for (MethodBody body : abc.bodies) if (body != changed)
            require(Arrays.equals(originals.get(body), body.getCodeBytes()), "Unrelated method changed");
        ((com.jpexs.decompiler.flash.tags.Tag)swf.getAbcList().get(0)).setModified(true);
        try (OutputStream out = Files.newOutputStream(Path.of(args[1]))) { swf.saveTo(out); }
        System.out.println("Preserved " + (abc.bodies.size()-1) + " unrelated method bodies");
    }
}
