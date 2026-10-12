using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// sbar.c: status bar code
public sealed partial class QuakeEngine
{
    // sbar.h: SBAR_HEIGHT
    public const int SBAR_HEIGHT = 24;

    /// <summary>num frame for '-' stats digit</summary>
    private const int STAT_MINUS = 10;

    /// <summary>if >= vid.numpages, no update needed</summary>
    public int sb_updates;

    public readonly QPic[][] sb_nums = [new QPic[11], new QPic[11]];
    public QPic sb_colon = null!, sb_slash = null!;
    public QPic sb_ibar = null!;
    public QPic sb_sbar = null!;
    public QPic sb_scorebar = null!;

    /// <summary>0 is active, 1 is owned, 2-5 are flashes</summary>
    public readonly QPic[][] sb_weapons = [.. Enumerable.Range(0, 7).Select(_ => new QPic[8])];

    public readonly QPic[] sb_ammo = new QPic[4];
    public readonly QPic[] sb_sigil = new QPic[4];
    public readonly QPic[] sb_armor = new QPic[3];
    public readonly QPic[] sb_items = new QPic[32];

    /// <summary>0 is gibbed, 1 is dead, 2-6 are alive; 0 is static, 1 is temporary animation</summary>
    public readonly QPic[][] sb_faces = [.. Enumerable.Range(0, 7).Select(_ => new QPic[2])];

    public QPic sb_face_invis = null!;
    public QPic sb_face_quad = null!;
    public QPic sb_face_invuln = null!;
    public QPic sb_face_invis_invuln = null!;

    public bool sb_showscores;

    /// <summary>scan lines to draw</summary>
    public int sb_lines;

    public readonly QPic[] rsb_invbar = new QPic[2];
    public readonly QPic[] rsb_weapons = new QPic[5];
    public readonly QPic[] rsb_items = new QPic[2];
    public readonly QPic[] rsb_ammo = new QPic[3];

    /// <summary>PGM 01/19/97 - team color border</summary>
    public QPic rsb_teambord = null!;

    /// <summary>MED 01/04/97 added two more weapons + 3 alternates for grenade launcher</summary>
    public readonly QPic[][] hsb_weapons = [.. Enumerable.Range(0, 7).Select(_ => new QPic[5])];

    /// <summary>MED 01/04/97 added array to simplify weapon parsing</summary>
    private static readonly int[] hipweapons = [HIT_LASER_CANNON_BIT, HIT_MJOLNIR_BIT, 4, HIT_PROXIMITY_GUN_BIT];

    /// <summary>MED 01/04/97 added hipnotic items array</summary>
    public readonly QPic[] hsb_items = new QPic[2];

    // sbar.c: Sbar_ShowScores
    // Tab key down
    public void Sbar_ShowScores()
    {
        if (sb_showscores)
        {
            return;
        }

        sb_showscores = true;
        sb_updates = 0;
    }

    // sbar.c: Sbar_DontShowScores
    // Tab key up
    public void Sbar_DontShowScores()
    {
        sb_showscores = false;
        sb_updates = 0;
    }

    // sbar.c: Sbar_Changed
    public void Sbar_Changed()
    {
        sb_updates = 0; // update next frame
    }

    // sbar.c: Sbar_Init
    public void Sbar_Init()
    {
        int i;

        for (i = 0; i < 10; i++)
        {
            sb_nums[0][i] = Draw_PicFromWad(va("num_%i", i));
            sb_nums[1][i] = Draw_PicFromWad(va("anum_%i", i));
        }

        sb_nums[0][10] = Draw_PicFromWad("num_minus");
        sb_nums[1][10] = Draw_PicFromWad("anum_minus");

        sb_colon = Draw_PicFromWad("num_colon");
        sb_slash = Draw_PicFromWad("num_slash");

        sb_weapons[0][0] = Draw_PicFromWad("inv_shotgun");
        sb_weapons[0][1] = Draw_PicFromWad("inv_sshotgun");
        sb_weapons[0][2] = Draw_PicFromWad("inv_nailgun");
        sb_weapons[0][3] = Draw_PicFromWad("inv_snailgun");
        sb_weapons[0][4] = Draw_PicFromWad("inv_rlaunch");
        sb_weapons[0][5] = Draw_PicFromWad("inv_srlaunch");
        sb_weapons[0][6] = Draw_PicFromWad("inv_lightng");

        sb_weapons[1][0] = Draw_PicFromWad("inv2_shotgun");
        sb_weapons[1][1] = Draw_PicFromWad("inv2_sshotgun");
        sb_weapons[1][2] = Draw_PicFromWad("inv2_nailgun");
        sb_weapons[1][3] = Draw_PicFromWad("inv2_snailgun");
        sb_weapons[1][4] = Draw_PicFromWad("inv2_rlaunch");
        sb_weapons[1][5] = Draw_PicFromWad("inv2_srlaunch");
        sb_weapons[1][6] = Draw_PicFromWad("inv2_lightng");

        for (i = 0; i < 5; i++)
        {
            sb_weapons[2 + i][0] = Draw_PicFromWad(va("inva%i_shotgun", i + 1));
            sb_weapons[2 + i][1] = Draw_PicFromWad(va("inva%i_sshotgun", i + 1));
            sb_weapons[2 + i][2] = Draw_PicFromWad(va("inva%i_nailgun", i + 1));
            sb_weapons[2 + i][3] = Draw_PicFromWad(va("inva%i_snailgun", i + 1));
            sb_weapons[2 + i][4] = Draw_PicFromWad(va("inva%i_rlaunch", i + 1));
            sb_weapons[2 + i][5] = Draw_PicFromWad(va("inva%i_srlaunch", i + 1));
            sb_weapons[2 + i][6] = Draw_PicFromWad(va("inva%i_lightng", i + 1));
        }

        sb_ammo[0] = Draw_PicFromWad("sb_shells");
        sb_ammo[1] = Draw_PicFromWad("sb_nails");
        sb_ammo[2] = Draw_PicFromWad("sb_rocket");
        sb_ammo[3] = Draw_PicFromWad("sb_cells");

        sb_armor[0] = Draw_PicFromWad("sb_armor1");
        sb_armor[1] = Draw_PicFromWad("sb_armor2");
        sb_armor[2] = Draw_PicFromWad("sb_armor3");

        sb_items[0] = Draw_PicFromWad("sb_key1");
        sb_items[1] = Draw_PicFromWad("sb_key2");
        sb_items[2] = Draw_PicFromWad("sb_invis");
        sb_items[3] = Draw_PicFromWad("sb_invuln");
        sb_items[4] = Draw_PicFromWad("sb_suit");
        sb_items[5] = Draw_PicFromWad("sb_quad");

        sb_sigil[0] = Draw_PicFromWad("sb_sigil1");
        sb_sigil[1] = Draw_PicFromWad("sb_sigil2");
        sb_sigil[2] = Draw_PicFromWad("sb_sigil3");
        sb_sigil[3] = Draw_PicFromWad("sb_sigil4");

        sb_faces[4][0] = Draw_PicFromWad("face1");
        sb_faces[4][1] = Draw_PicFromWad("face_p1");
        sb_faces[3][0] = Draw_PicFromWad("face2");
        sb_faces[3][1] = Draw_PicFromWad("face_p2");
        sb_faces[2][0] = Draw_PicFromWad("face3");
        sb_faces[2][1] = Draw_PicFromWad("face_p3");
        sb_faces[1][0] = Draw_PicFromWad("face4");
        sb_faces[1][1] = Draw_PicFromWad("face_p4");
        sb_faces[0][0] = Draw_PicFromWad("face5");
        sb_faces[0][1] = Draw_PicFromWad("face_p5");

        sb_face_invis = Draw_PicFromWad("face_invis");
        sb_face_invuln = Draw_PicFromWad("face_invul2");
        sb_face_invis_invuln = Draw_PicFromWad("face_inv2");
        sb_face_quad = Draw_PicFromWad("face_quad");

        Cmd_AddCommand("+showscores", Sbar_ShowScores);
        Cmd_AddCommand("-showscores", Sbar_DontShowScores);

        sb_sbar = Draw_PicFromWad("sbar");
        sb_ibar = Draw_PicFromWad("ibar");
        sb_scorebar = Draw_PicFromWad("scorebar");

        // MED 01/04/97 added new hipnotic weapons
        if (com_argv.hipnotic)
        {
            hsb_weapons[0][0] = Draw_PicFromWad("inv_laser");
            hsb_weapons[0][1] = Draw_PicFromWad("inv_mjolnir");
            hsb_weapons[0][2] = Draw_PicFromWad("inv_gren_prox");
            hsb_weapons[0][3] = Draw_PicFromWad("inv_prox_gren");
            hsb_weapons[0][4] = Draw_PicFromWad("inv_prox");

            hsb_weapons[1][0] = Draw_PicFromWad("inv2_laser");
            hsb_weapons[1][1] = Draw_PicFromWad("inv2_mjolnir");
            hsb_weapons[1][2] = Draw_PicFromWad("inv2_gren_prox");
            hsb_weapons[1][3] = Draw_PicFromWad("inv2_prox_gren");
            hsb_weapons[1][4] = Draw_PicFromWad("inv2_prox");

            for (i = 0; i < 5; i++)
            {
                hsb_weapons[2 + i][0] = Draw_PicFromWad(va("inva%i_laser", i + 1));
                hsb_weapons[2 + i][1] = Draw_PicFromWad(va("inva%i_mjolnir", i + 1));
                hsb_weapons[2 + i][2] = Draw_PicFromWad(va("inva%i_gren_prox", i + 1));
                hsb_weapons[2 + i][3] = Draw_PicFromWad(va("inva%i_prox_gren", i + 1));
                hsb_weapons[2 + i][4] = Draw_PicFromWad(va("inva%i_prox", i + 1));
            }

            hsb_items[0] = Draw_PicFromWad("sb_wsuit");
            hsb_items[1] = Draw_PicFromWad("sb_eshld");
        }

        if (com_argv.rogue)
        {
            rsb_invbar[0] = Draw_PicFromWad("r_invbar1");
            rsb_invbar[1] = Draw_PicFromWad("r_invbar2");

            rsb_weapons[0] = Draw_PicFromWad("r_lava");
            rsb_weapons[1] = Draw_PicFromWad("r_superlava");
            rsb_weapons[2] = Draw_PicFromWad("r_gren");
            rsb_weapons[3] = Draw_PicFromWad("r_multirock");
            rsb_weapons[4] = Draw_PicFromWad("r_plasma");

            rsb_items[0] = Draw_PicFromWad("r_shield1");
            rsb_items[1] = Draw_PicFromWad("r_agrav1");

            // PGM 01/19/97 - team color border
            rsb_teambord = Draw_PicFromWad("r_teambord");

            // PGM 01/19/97 - team color border
            rsb_ammo[0] = Draw_PicFromWad("r_ammolava");
            rsb_ammo[1] = Draw_PicFromWad("r_ammomulti");
            rsb_ammo[2] = Draw_PicFromWad("r_ammoplasma");
        }
    }

    // =============================================================================

    // drawing routines are relative to the status bar location

    // sbar.c: Sbar_DrawPic
    public void Sbar_DrawPic(int x, int y, QPic pic)
    {
        if (cl.gametype == GAME_DEATHMATCH)
        {
            Draw_Pic(x /* + ((vid.width - 320)>>1)*/, y + (int)(vid.height - SBAR_HEIGHT), pic);
        }
        else
        {
            Draw_Pic(x + (int)((vid.width - 320) >> 1), y + (int)(vid.height - SBAR_HEIGHT), pic);
        }
    }

    // sbar.c: Sbar_DrawTransPic
    public void Sbar_DrawTransPic(int x, int y, QPic pic)
    {
        if (cl.gametype == GAME_DEATHMATCH)
        {
            Draw_TransPic(x /*+ ((vid.width - 320)>>1)*/, y + (int)(vid.height - SBAR_HEIGHT), pic);
        }
        else
        {
            Draw_TransPic(x + (int)((vid.width - 320) >> 1), y + (int)(vid.height - SBAR_HEIGHT), pic);
        }
    }

    // sbar.c: Sbar_DrawCharacter
    // Draws one solid graphics character
    public void Sbar_DrawCharacter(int x, int y, int num)
    {
        if (cl.gametype == GAME_DEATHMATCH)
        {
            Draw_Character(x /*+ ((vid.width - 320)>>1) */ + 4, y + (int)vid.height - SBAR_HEIGHT, num);
        }
        else
        {
            Draw_Character(x + (int)((vid.width - 320) >> 1) + 4, y + (int)vid.height - SBAR_HEIGHT, num);
        }
    }

    // sbar.c: Sbar_DrawString
    public void Sbar_DrawString(int x, int y, string str)
    {
        if (cl.gametype == GAME_DEATHMATCH)
        {
            Draw_String(x /*+ ((vid.width - 320)>>1)*/, y + (int)vid.height - SBAR_HEIGHT, str);
        }
        else
        {
            Draw_String(x + (int)((vid.width - 320) >> 1), y + (int)vid.height - SBAR_HEIGHT, str);
        }
    }

    // sbar.c: Sbar_itoa
    public static string Sbar_itoa(int num)
    {
        var str = new System.Text.StringBuilder(12);
        int pow10;
        int dig;

        unchecked
        {
            if (num < 0)
            {
                str.Append('-');
                num = -num;
            }

            for (pow10 = 10; num >= pow10; pow10 *= 10)
            {
            }

            do
            {
                pow10 /= 10;
                dig = num / pow10;
                str.Append((char)('0' + dig));
                num -= dig * pow10;
            }
            while (pow10 != 1);
        }

        return str.ToString();
    }

    // sbar.c: Sbar_DrawNum
    public void Sbar_DrawNum(int x, int y, int num, int digits, int color)
    {
        string str;
        int ptr;
        int l, frame;

        str = Sbar_itoa(num);
        l = str.Length;
        ptr = 0;
        if (l > digits)
        {
            ptr += l - digits;
        }

        if (l < digits)
        {
            x += (digits - l) * 24;
        }

        while (ptr < l)
        {
            if (str[ptr] == '-')
            {
                frame = STAT_MINUS;
            }
            else
            {
                frame = str[ptr] - '0';
            }

            Sbar_DrawTransPic(x, y, sb_nums[color][frame]);
            x += 24;
            ptr++;
        }
    }

    // =============================================================================

    public readonly int[] fragsort = new int[MAX_SCOREBOARD];

    public int scoreboardlines;

    // sbar.c: Sbar_SortFrags
    public void Sbar_SortFrags()
    {
        int i, j, k;

        // sort by frags
        scoreboardlines = 0;
        for (i = 0; i < cl.maxclients; i++)
        {
            if (Ch(cl.scores![i].name, 0) != 0)
            {
                fragsort[scoreboardlines] = i;
                scoreboardlines++;
            }
        }

        for (i = 0; i < scoreboardlines; i++)
        {
            for (j = 0; j < scoreboardlines - 1 - i; j++)
            {
                if (cl.scores![fragsort[j]].frags < cl.scores[fragsort[j + 1]].frags)
                {
                    k = fragsort[j];
                    fragsort[j] = fragsort[j + 1];
                    fragsort[j + 1] = k;
                }
            }
        }
    }

    // sbar.c: Sbar_ColorForMap
    public static int Sbar_ColorForMap(int m) => m < 128 ? m + 8 : m + 8;

    // sbar.c: Sbar_SoloScoreboard
    public void Sbar_SoloScoreboard()
    {
        string str;
        int minutes, seconds, tens, units;
        int l;

        str = CFormat.Sprintf("Monsters:%3i /%3i", cl.stats[STAT_MONSTERS], cl.stats[STAT_TOTALMONSTERS]);
        Sbar_DrawString(8, 4, str);

        str = CFormat.Sprintf("Secrets :%3i /%3i", cl.stats[STAT_SECRETS], cl.stats[STAT_TOTALSECRETS]);
        Sbar_DrawString(8, 12, str);

        // time
        minutes = (int)(cl.time / 60);
        seconds = (int)(cl.time - (60 * minutes));
        tens = seconds / 10;
        units = seconds - (10 * tens);
        str = CFormat.Sprintf("Time :%3i:%i%i", minutes, tens, units);
        Sbar_DrawString(184, 4, str);

        // draw level name
        l = Q_strlen(cl.levelname);
        Sbar_DrawString(232 - (l * 4), 12, cl.levelname);
    }

    // sbar.c: Sbar_DrawScoreboard
    public void Sbar_DrawScoreboard()
    {
        Sbar_SoloScoreboard();
        if (cl.gametype == GAME_DEATHMATCH)
        {
            Sbar_DeathmatchOverlay();
        }
    }

    // =============================================================================

    // sbar.c: Sbar_DrawInventory
    public void Sbar_DrawInventory()
    {
        int i;
        string num;
        float time;
        int flashon;

        if (com_argv.rogue)
        {
            if (cl.stats[STAT_ACTIVEWEAPON] >= RIT_LAVA_NAILGUN)
            {
                Sbar_DrawPic(0, -24, rsb_invbar[0]);
            }
            else
            {
                Sbar_DrawPic(0, -24, rsb_invbar[1]);
            }
        }
        else
        {
            Sbar_DrawPic(0, -24, sb_ibar);
        }

        // weapons
        for (i = 0; i < 7; i++)
        {
            if ((cl.items & (IT_SHOTGUN << i)) != 0)
            {
                time = cl.item_gettime[i];
                flashon = (int)((cl.time - time) * 10);
                if (flashon >= 10)
                {
                    if (cl.stats[STAT_ACTIVEWEAPON] == (IT_SHOTGUN << i))
                    {
                        flashon = 1;
                    }
                    else
                    {
                        flashon = 0;
                    }
                }
                else
                {
                    flashon = (flashon % 5) + 2;
                }

                Sbar_DrawPic(i * 24, -16, sb_weapons[flashon][i]);

                if (flashon > 1)
                {
                    sb_updates = 0; // force update to remove flash
                }
            }
        }

        // MED 01/04/97
        // hipnotic weapons
        if (com_argv.hipnotic)
        {
            int grenadeflashing = 0;
            for (i = 0; i < 4; i++)
            {
                if ((cl.items & (1 << hipweapons[i])) != 0)
                {
                    time = cl.item_gettime[hipweapons[i]];
                    flashon = (int)((cl.time - time) * 10);
                    if (flashon >= 10)
                    {
                        if (cl.stats[STAT_ACTIVEWEAPON] == (1 << hipweapons[i]))
                        {
                            flashon = 1;
                        }
                        else
                        {
                            flashon = 0;
                        }
                    }
                    else
                    {
                        flashon = (flashon % 5) + 2;
                    }

                    // check grenade launcher
                    if (i == 2)
                    {
                        if ((cl.items & HIT_PROXIMITY_GUN) != 0)
                        {
                            if (flashon != 0)
                            {
                                grenadeflashing = 1;
                                Sbar_DrawPic(96, -16, hsb_weapons[flashon][2]);
                            }
                        }
                    }
                    else if (i == 3)
                    {
                        if ((cl.items & (IT_SHOTGUN << 4)) != 0)
                        {
                            if (flashon != 0 && grenadeflashing == 0)
                            {
                                Sbar_DrawPic(96, -16, hsb_weapons[flashon][3]);
                            }
                            else if (grenadeflashing == 0)
                            {
                                Sbar_DrawPic(96, -16, hsb_weapons[0][3]);
                            }
                        }
                        else
                        {
                            Sbar_DrawPic(96, -16, hsb_weapons[flashon][4]);
                        }
                    }
                    else
                    {
                        Sbar_DrawPic(176 + (i * 24), -16, hsb_weapons[flashon][i]);
                    }

                    if (flashon > 1)
                    {
                        sb_updates = 0; // force update to remove flash
                    }
                }
            }
        }

        if (com_argv.rogue)
        {
            // check for powered up weapon.
            if (cl.stats[STAT_ACTIVEWEAPON] >= RIT_LAVA_NAILGUN)
            {
                for (i = 0; i < 5; i++)
                {
                    if (cl.stats[STAT_ACTIVEWEAPON] == (RIT_LAVA_NAILGUN << i))
                    {
                        Sbar_DrawPic((i + 2) * 24, -16, rsb_weapons[i]);
                    }
                }
            }
        }

        // ammo counts
        for (i = 0; i < 4; i++)
        {
            num = CFormat.Sprintf("%3i", cl.stats[STAT_SHELLS + i]);
            if (Ch(num, 0) != ' ')
            {
                Sbar_DrawCharacter((((6 * i) + 1) * 8) - 2, -24, 18 + Ch(num, 0) - '0');
            }

            if (Ch(num, 1) != ' ')
            {
                Sbar_DrawCharacter((((6 * i) + 2) * 8) - 2, -24, 18 + Ch(num, 1) - '0');
            }

            if (Ch(num, 2) != ' ')
            {
                Sbar_DrawCharacter((((6 * i) + 3) * 8) - 2, -24, 18 + Ch(num, 2) - '0');
            }
        }

        flashon = 0;

        // items
        for (i = 0; i < 6; i++)
        {
            if ((cl.items & (1 << (17 + i))) != 0)
            {
                time = cl.item_gettime[17 + i];
                if (time != 0 && time > cl.time - 2 && flashon != 0)
                {
                    // flash frame
                    sb_updates = 0;
                }
                else
                {
                    // MED 01/04/97 changed keys
                    if (!com_argv.hipnotic || (i > 1))
                    {
                        Sbar_DrawPic(192 + (i * 16), -16, sb_items[i]);
                    }
                }

                if (time != 0 && time > cl.time - 2)
                {
                    sb_updates = 0;
                }
            }
        }

        // MED 01/04/97 added hipnotic items
        // hipnotic items
        if (com_argv.hipnotic)
        {
            for (i = 0; i < 2; i++)
            {
                if ((cl.items & (1 << (24 + i))) != 0)
                {
                    time = cl.item_gettime[24 + i];
                    if (time != 0 && time > cl.time - 2 && flashon != 0)
                    {
                        // flash frame
                        sb_updates = 0;
                    }
                    else
                    {
                        Sbar_DrawPic(288 + (i * 16), -16, hsb_items[i]);
                    }

                    if (time != 0 && time > cl.time - 2)
                    {
                        sb_updates = 0;
                    }
                }
            }
        }

        if (com_argv.rogue)
        {
            // new rogue items
            for (i = 0; i < 2; i++)
            {
                if ((cl.items & (1 << (29 + i))) != 0)
                {
                    time = cl.item_gettime[29 + i];

                    if (time != 0 && time > cl.time - 2 && flashon != 0)
                    {
                        // flash frame
                        sb_updates = 0;
                    }
                    else
                    {
                        Sbar_DrawPic(288 + (i * 16), -16, rsb_items[i]);
                    }

                    if (time != 0 && time > cl.time - 2)
                    {
                        sb_updates = 0;
                    }
                }
            }
        }
        else
        {
            // sigils
            for (i = 0; i < 4; i++)
            {
                if ((cl.items & (1 << (28 + i))) != 0)
                {
                    time = cl.item_gettime[28 + i];
                    if (time != 0 && time > cl.time - 2 && flashon != 0)
                    {
                        // flash frame
                        sb_updates = 0;
                    }
                    else
                    {
                        Sbar_DrawPic(320 - 32 + (i * 8), -16, sb_sigil[i]);
                    }

                    if (time != 0 && time > cl.time - 2)
                    {
                        sb_updates = 0;
                    }
                }
            }
        }
    }

    // =============================================================================

    // sbar.c: Sbar_DrawFrags
    public void Sbar_DrawFrags()
    {
        int i, k, l;
        int top, bottom;
        int x, y, f;
        int xofs;
        string num;
        Client.Scoreboard s;

        Sbar_SortFrags();

        // draw the text
        l = scoreboardlines <= 4 ? scoreboardlines : 4;

        x = 23;
        if (cl.gametype == GAME_DEATHMATCH)
        {
            xofs = 0;
        }
        else
        {
            xofs = (int)((vid.width - 320) >> 1);
        }

        y = (int)vid.height - SBAR_HEIGHT - 23;

        for (i = 0; i < l; i++)
        {
            k = fragsort[i];
            s = cl.scores![k];
            if (Ch(s.name, 0) == 0)
            {
                continue;
            }

            // draw background
            top = s.colors & 0xf0;
            bottom = (s.colors & 15) << 4;
            top = Sbar_ColorForMap(top);
            bottom = Sbar_ColorForMap(bottom);

            Draw_Fill(xofs + (x * 8) + 10, y, 28, 4, top);
            Draw_Fill(xofs + (x * 8) + 10, y + 4, 28, 3, bottom);

            // draw number
            f = s.frags;
            num = CFormat.Sprintf("%3i", f);

            Sbar_DrawCharacter((x + 1) * 8, -24, Ch(num, 0));
            Sbar_DrawCharacter((x + 2) * 8, -24, Ch(num, 1));
            Sbar_DrawCharacter((x + 3) * 8, -24, Ch(num, 2));

            if (k == cl.viewentity - 1)
            {
                Sbar_DrawCharacter((x * 8) + 2, -24, 16);
                Sbar_DrawCharacter(((x + 4) * 8) - 4, -24, 17);
            }

            x += 4;
        }
    }

    // =============================================================================

    // sbar.c: Sbar_DrawFace
    public void Sbar_DrawFace()
    {
        int f, anim;

        // PGM 01/19/97 - team color drawing
        // PGM 03/02/97 - fixed so color swatch only appears in CTF modes
        if (com_argv.rogue &&
            (cl.maxclients != 1) &&
            (teamplay.value > 3) &&
            (teamplay.value < 7))
        {
            int top, bottom;
            int xofs;
            string num;
            Client.Scoreboard s;

            s = cl.scores![cl.viewentity - 1];

            // draw background
            top = s.colors & 0xf0;
            bottom = (s.colors & 15) << 4;
            top = Sbar_ColorForMap(top);
            bottom = Sbar_ColorForMap(bottom);

            if (cl.gametype == GAME_DEATHMATCH)
            {
                xofs = 113;
            }
            else
            {
                xofs = (int)((vid.width - 320) >> 1) + 113;
            }

            Sbar_DrawPic(112, 0, rsb_teambord);
            Draw_Fill(xofs, (int)vid.height - SBAR_HEIGHT + 3, 22, 9, top);
            Draw_Fill(xofs, (int)vid.height - SBAR_HEIGHT + 12, 22, 9, bottom);

            // draw number
            f = s.frags;
            num = CFormat.Sprintf("%3i", f);

            if (top == 8)
            {
                if (Ch(num, 0) != ' ')
                {
                    Sbar_DrawCharacter(109, 3, 18 + Ch(num, 0) - '0');
                }

                if (Ch(num, 1) != ' ')
                {
                    Sbar_DrawCharacter(116, 3, 18 + Ch(num, 1) - '0');
                }

                if (Ch(num, 2) != ' ')
                {
                    Sbar_DrawCharacter(123, 3, 18 + Ch(num, 2) - '0');
                }
            }
            else
            {
                Sbar_DrawCharacter(109, 3, Ch(num, 0));
                Sbar_DrawCharacter(116, 3, Ch(num, 1));
                Sbar_DrawCharacter(123, 3, Ch(num, 2));
            }

            return;
        }

        // PGM 01/19/97 - team color drawing
        if ((cl.items & (IT_INVISIBILITY | IT_INVULNERABILITY))
            == (IT_INVISIBILITY | IT_INVULNERABILITY))
        {
            Sbar_DrawPic(112, 0, sb_face_invis_invuln);
            return;
        }

        if ((cl.items & IT_QUAD) != 0)
        {
            Sbar_DrawPic(112, 0, sb_face_quad);
            return;
        }

        if ((cl.items & IT_INVISIBILITY) != 0)
        {
            Sbar_DrawPic(112, 0, sb_face_invis);
            return;
        }

        if ((cl.items & IT_INVULNERABILITY) != 0)
        {
            Sbar_DrawPic(112, 0, sb_face_invuln);
            return;
        }

        if (cl.stats[STAT_HEALTH] >= 100)
        {
            f = 4;
        }
        else
        {
            f = cl.stats[STAT_HEALTH] / 20;
        }

        if (cl.time <= cl.faceanimtime)
        {
            anim = 1;
            sb_updates = 0; // make sure the anim gets drawn over
        }
        else
        {
            anim = 0;
        }

        Sbar_DrawPic(112, 0, sb_faces[f][anim]);
    }

    // sbar.c: Sbar_Draw
    public void Sbar_Draw()
    {
        if (scr_con_current == vid.height)
        {
            return; // console is full screen
        }

        if (sb_updates >= vid.numpages)
        {
            return;
        }

        scr_copyeverything = 1;

        sb_updates++;

        if (sb_lines != 0 && vid.width > 320)
        {
            Draw_TileClear(0, (int)vid.height - sb_lines, (int)vid.width, sb_lines);
        }

        if (sb_lines > 24)
        {
            Sbar_DrawInventory();
            if (cl.maxclients != 1)
            {
                Sbar_DrawFrags();
            }
        }

        if (sb_showscores || cl.stats[STAT_HEALTH] <= 0)
        {
            Sbar_DrawPic(0, 0, sb_scorebar);
            Sbar_DrawScoreboard();
            sb_updates = 0;
        }
        else if (sb_lines != 0)
        {
            Sbar_DrawPic(0, 0, sb_sbar);

            // keys (hipnotic only)
            // MED 01/04/97 moved keys here so they would not be overwritten
            if (com_argv.hipnotic)
            {
                if ((cl.items & IT_KEY1) != 0)
                {
                    Sbar_DrawPic(209, 3, sb_items[0]);
                }

                if ((cl.items & IT_KEY2) != 0)
                {
                    Sbar_DrawPic(209, 12, sb_items[1]);
                }
            }

            // armor
            if ((cl.items & IT_INVULNERABILITY) != 0)
            {
                Sbar_DrawNum(24, 0, 666, 3, 1);
                Sbar_DrawPic(0, 0, draw_disc!);
            }
            else
            {
                if (com_argv.rogue)
                {
                    Sbar_DrawNum(24, 0, cl.stats[STAT_ARMOR], 3,
                        cl.stats[STAT_ARMOR] <= 25 ? 1 : 0);
                    if ((cl.items & RIT_ARMOR3) != 0)
                    {
                        Sbar_DrawPic(0, 0, sb_armor[2]);
                    }
                    else if ((cl.items & RIT_ARMOR2) != 0)
                    {
                        Sbar_DrawPic(0, 0, sb_armor[1]);
                    }
                    else if ((cl.items & RIT_ARMOR1) != 0)
                    {
                        Sbar_DrawPic(0, 0, sb_armor[0]);
                    }
                }
                else
                {
                    Sbar_DrawNum(24, 0, cl.stats[STAT_ARMOR], 3,
                        cl.stats[STAT_ARMOR] <= 25 ? 1 : 0);
                    if ((cl.items & IT_ARMOR3) != 0)
                    {
                        Sbar_DrawPic(0, 0, sb_armor[2]);
                    }
                    else if ((cl.items & IT_ARMOR2) != 0)
                    {
                        Sbar_DrawPic(0, 0, sb_armor[1]);
                    }
                    else if ((cl.items & IT_ARMOR1) != 0)
                    {
                        Sbar_DrawPic(0, 0, sb_armor[0]);
                    }
                }
            }

            // face
            Sbar_DrawFace();

            // health
            Sbar_DrawNum(136, 0, cl.stats[STAT_HEALTH], 3,
                cl.stats[STAT_HEALTH] <= 25 ? 1 : 0);

            // ammo icon
            if (com_argv.rogue)
            {
                if ((cl.items & RIT_SHELLS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[0]);
                }
                else if ((cl.items & RIT_NAILS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[1]);
                }
                else if ((cl.items & RIT_ROCKETS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[2]);
                }
                else if ((cl.items & RIT_CELLS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[3]);
                }
                else if ((cl.items & RIT_LAVA_NAILS) != 0)
                {
                    Sbar_DrawPic(224, 0, rsb_ammo[0]);
                }
                else if ((cl.items & RIT_PLASMA_AMMO) != 0)
                {
                    Sbar_DrawPic(224, 0, rsb_ammo[1]);
                }
                else if ((cl.items & RIT_MULTI_ROCKETS) != 0)
                {
                    Sbar_DrawPic(224, 0, rsb_ammo[2]);
                }
            }
            else
            {
                if ((cl.items & IT_SHELLS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[0]);
                }
                else if ((cl.items & IT_NAILS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[1]);
                }
                else if ((cl.items & IT_ROCKETS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[2]);
                }
                else if ((cl.items & IT_CELLS) != 0)
                {
                    Sbar_DrawPic(224, 0, sb_ammo[3]);
                }
            }

            Sbar_DrawNum(248, 0, cl.stats[STAT_AMMO], 3,
                cl.stats[STAT_AMMO] <= 10 ? 1 : 0);
        }

        if (vid.width > 320)
        {
            if (cl.gametype == GAME_DEATHMATCH)
            {
                Sbar_MiniDeathmatchOverlay();
            }
        }
    }

    // =============================================================================

    // sbar.c: Sbar_IntermissionNumber
    public void Sbar_IntermissionNumber(int x, int y, int num, int digits, int color)
    {
        string str;
        int ptr;
        int l, frame;

        str = Sbar_itoa(num);
        l = str.Length;
        ptr = 0;
        if (l > digits)
        {
            ptr += l - digits;
        }

        if (l < digits)
        {
            x += (digits - l) * 24;
        }

        while (ptr < l)
        {
            if (str[ptr] == '-')
            {
                frame = STAT_MINUS;
            }
            else
            {
                frame = str[ptr] - '0';
            }

            Draw_TransPic(x, y, sb_nums[color][frame]);
            x += 24;
            ptr++;
        }
    }

    // sbar.c: Sbar_DeathmatchOverlay
    public void Sbar_DeathmatchOverlay()
    {
        QPic pic;
        int i, k, l;
        int top, bottom;
        int x, y, f;
        string num;
        Client.Scoreboard s;

        scr_copyeverything = 1;
        scr_fullupdate = 0;

        pic = Draw_CachePic("gfx/ranking.lmp");
        M_DrawPic((320 - pic.width) / 2, 8, pic);

        // scores
        Sbar_SortFrags();

        // draw the text
        l = scoreboardlines;

        x = 80 + (int)((vid.width - 320) >> 1);
        y = 40;
        for (i = 0; i < l; i++)
        {
            k = fragsort[i];
            s = cl.scores![k];
            if (Ch(s.name, 0) == 0)
            {
                continue;
            }

            // draw background
            top = s.colors & 0xf0;
            bottom = (s.colors & 15) << 4;
            top = Sbar_ColorForMap(top);
            bottom = Sbar_ColorForMap(bottom);

            Draw_Fill(x, y, 40, 4, top);
            Draw_Fill(x, y + 4, 40, 4, bottom);

            // draw number
            f = s.frags;
            num = CFormat.Sprintf("%3i", f);

            Draw_Character(x + 8, y, Ch(num, 0));
            Draw_Character(x + 16, y, Ch(num, 1));
            Draw_Character(x + 24, y, Ch(num, 2));

            if (k == cl.viewentity - 1)
            {
                Draw_Character(x - 8, y, 12);
            }

            // draw name
            Draw_String(x + 64, y, s.name);

            y += 10;
        }
    }

    // sbar.c: Sbar_MiniDeathmatchOverlay
    public void Sbar_MiniDeathmatchOverlay()
    {
        int i, k;
        int top, bottom;
        int x, y, f;
        string num;
        Client.Scoreboard s;
        int numlines;

        if (vid.width < 512 || sb_lines == 0)
        {
            return;
        }

        scr_copyeverything = 1;
        scr_fullupdate = 0;

        // scores
        Sbar_SortFrags();

        // draw the text
        y = (int)vid.height - sb_lines;
        numlines = sb_lines / 8;
        if (numlines < 3)
        {
            return;
        }

        // find us
        for (i = 0; i < scoreboardlines; i++)
        {
            if (fragsort[i] == cl.viewentity - 1)
            {
                break;
            }
        }

        if (i == scoreboardlines)
        {
            // we're not there
            i = 0;
        }
        else
        {
            // figure out start
            i -= numlines / 2;
        }

        if (i > scoreboardlines - numlines)
        {
            i = scoreboardlines - numlines;
        }

        if (i < 0)
        {
            i = 0;
        }

        x = 324;
        for (/* */; i < scoreboardlines && y < vid.height - 8; i++)
        {
            k = fragsort[i];
            s = cl.scores![k];
            if (Ch(s.name, 0) == 0)
            {
                continue;
            }

            // draw background
            top = s.colors & 0xf0;
            bottom = (s.colors & 15) << 4;
            top = Sbar_ColorForMap(top);
            bottom = Sbar_ColorForMap(bottom);

            Draw_Fill(x, y + 1, 40, 3, top);
            Draw_Fill(x, y + 4, 40, 4, bottom);

            // draw number
            f = s.frags;
            num = CFormat.Sprintf("%3i", f);

            Draw_Character(x + 8, y, Ch(num, 0));
            Draw_Character(x + 16, y, Ch(num, 1));
            Draw_Character(x + 24, y, Ch(num, 2));

            if (k == cl.viewentity - 1)
            {
                Draw_Character(x, y, 16);
                Draw_Character(x + 32, y, 17);
            }

            // draw name
            Draw_String(x + 48, y, s.name);

            y += 8;
        }
    }

    // sbar.c: Sbar_IntermissionOverlay
    public void Sbar_IntermissionOverlay()
    {
        QPic pic;
        int dig;
        int num;

        scr_copyeverything = 1;
        scr_fullupdate = 0;

        if (cl.gametype == GAME_DEATHMATCH)
        {
            Sbar_DeathmatchOverlay();
            return;
        }

        pic = Draw_CachePic("gfx/complete.lmp");
        Draw_Pic(64, 24, pic);

        pic = Draw_CachePic("gfx/inter.lmp");
        Draw_TransPic(0, 56, pic);

        // time
        dig = cl.completed_time / 60;
        Sbar_IntermissionNumber(160, 64, dig, 3, 0);
        num = cl.completed_time - (dig * 60);
        Draw_TransPic(234, 64, sb_colon);
        Draw_TransPic(246, 64, sb_nums[0][num / 10]);
        Draw_TransPic(266, 64, sb_nums[0][num % 10]);

        Sbar_IntermissionNumber(160, 104, cl.stats[STAT_SECRETS], 3, 0);
        Draw_TransPic(232, 104, sb_slash);
        Sbar_IntermissionNumber(240, 104, cl.stats[STAT_TOTALSECRETS], 3, 0);

        Sbar_IntermissionNumber(160, 144, cl.stats[STAT_MONSTERS], 3, 0);
        Draw_TransPic(232, 144, sb_slash);
        Sbar_IntermissionNumber(240, 144, cl.stats[STAT_TOTALMONSTERS], 3, 0);
    }

    // sbar.c: Sbar_FinaleOverlay
    public void Sbar_FinaleOverlay()
    {
        QPic pic;

        scr_copyeverything = 1;

        pic = Draw_CachePic("gfx/finale.lmp");
        Draw_TransPic((int)((vid.width - pic.width) / 2), 16, pic);
    }
}
