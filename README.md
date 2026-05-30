# ⏰ ScheduledRestart — Unturned timed restarts + Discord control

ปลั๊กอิน RocketMod ที่รีสตาร์ทเซิร์ฟเวอร์ตามเวลา (ค่าเริ่มต้น **06:00 น. ไทย**) พร้อม
แจ้งเตือนในเกม, บันทึกโลกก่อนปิด, ล็อกคำสั่งช่วงก่อนรี และคุยกับ **Discord bot** ผ่าน
**MySQL ตัวเดียวกับ SellVault** — โชว์สถานะผู้เล่น + สั่งรีฉุกเฉินจาก Discord ได้

```
┌────────────────────────┐        sr_status (players, next_restart)        ┌───────────────────┐
│  Unturned plugin (C#)  │  ───────────────────────────────────────────▶  │  Discord bot (py) │
│  ScheduledRestart      │        sr_events (warnings, back-online)        │  embed + /status  │
│                        │  ◀───────────────────────────────────────────  │  /restart ...     │
└────────────────────────┘        sr_commands (emergency restart/cancel)   └───────────────────┘
                              shared MySQL  (เดียวกับ SellVault / RedeemCode)
```

## ทำอะไรได้
- 🕕 **รีสตาร์ทรายวันเวลาตายตัว** — `DailyRestartTimes: ["06:00"]` (ใส่หลายเวลาได้) คำนวณเป็น
  เวลาไทย (UTC+7) ไม่ขึ้นกับ timezone ของเครื่อง Linux
- 📢 **เตือนในเกม** ก่อน **10 นาที** และ **1 นาที** + **countdown 10→1 วินาที**
- 💾 **บันทึกโลก (`SaveManager.save`) ก่อนปิดเซิร์ฟเวอร์**เสมอ
- ⛔ **ล็อกทุกคำสั่ง**ของผู้เล่นทั่วไปช่วง 10 นาทีก่อนรี (แอดมิน + perm `scheduledrestart.bypass` ผ่านได้)
- 🟢 **Discord embed** โชว์ `Players: 3/100` + เวลารีรอบถัดไป อัปเดตทุก 30 วิ + `/status`
- 🚨 **รีฉุกเฉินจาก Discord:** `/restart in <นาที>`, `/restart now`, `/restart cancel` (เฉพาะแอดมิน)
  — และในเกมก็มี `/srestart <now|cancel|นาที>`

---

## 1) ปลั๊กอิน (C#) — build บน Windows แล้วอัปขึ้น panel

> เซิร์ฟอยู่บน **RestoreMonarchy panel (Linux)** — เรา **build บนเครื่อง Windows** ที่มีไฟล์เกม
> แล้วอัป `.dll` ขึ้น panel เท่านั้น (Mono รัน Harmony + MySql.Data ได้ เหมือน AutoCleanup/SellVault)

### Build
```powershell
# วาง DLL deps (ทำให้แล้ว — มีอยู่ใน lib/)
#   ScheduledRestart/lib/0Harmony.dll      (จาก unturned_AutoClear)
#   ScheduledRestart/lib/MySql.Data.dll    (จาก SellVault)
powershell -ExecutionPolicy Bypass -File ScheduledRestart\build.ps1
# -> ScheduledRestart\bin\ScheduledRestart.dll
```
ถ้า Unturned ไม่ได้อยู่ที่ `D:\SteamLibrary\...` แก้ path บนหัว `build.ps1`

### Deploy ขึ้น panel
1. อัป `ScheduledRestart.dll` → `Rocket/Plugins/`
2. ให้แน่ใจว่า server มี (ปกติมีอยู่แล้วจาก AutoCleanup/SellVault):
   - `Rocket/Libraries/0Harmony.dll`
   - `Rocket/Libraries/MySql.Data.dll`
3. รันเซิร์ฟ 1 ครั้งให้สร้าง config → แก้ `Rocket/Plugins/ScheduledRestart/ScheduledRestart.configuration.xml`
   - `Database.ConnectionString` = ตัวเดียวกับ SellVault
   - `DailyRestartTimes` (เพิ่มได้ เช่น `06:00`, `18:00`)
   - `WarnBeforeSeconds` (default `600`, `60`), `CountdownSeconds`, `LockdownSeconds`
4. ⚠️ **เปิด "Auto Restart / keep online" ใน RestoreMonarchy panel** — `Provider.shutdown()` แค่ปิด
   process; panel เป็นตัวเด้งเซิร์ฟกลับขึ้นมา ถ้าไม่เปิด เซิร์ฟจะดับค้าง

---

## 2) Discord bot (Python)

ใช้ MySQL เดียวกับปลั๊กอิน เลือกได้ 2 แบบ:

### แบบ A — เสียบเข้าบอทเดิม (แนะนำ)
ก๊อป `restart_cog.py`, `restart_db.py`, `restart_config.py` ไปไว้กับบอทเดิม แล้วใน `bot.py`:
```python
from restart_cog import RestartCog
await bot.add_cog(RestartCog(bot))   # ใน setup_hook(); แล้ว sync tree ตามปกติ
```
เพิ่มค่าใน `.env` เดิม: `STATUS_CHANNEL_ID`, `ANNOUNCE_CHANNEL_ID`, `SR_PREFIX=sr_`, `TZ_OFFSET_HOURS=7`, `SERVER_NAME`

### แบบ B — รันแยก standalone
```bash
cd DiscordBot
pip install -r requirements.txt
copy .env.example .env      # แล้วแก้ค่า
python standalone_bot.py
```

> บอท + MySQL ต้องอยู่ที่ที่เข้าถึง DB เดียวกับเซิร์ฟเกมได้ (เหมือนบอท SellVault ปัจจุบัน)

---

## 3) Flow การรีสตาร์ท

```
T-10 นาที : "⚠ เซิร์ฟจะรีสตาร์ทในอีก 10 นาที"  + ⛔ ล็อกคำสั่งเริ่ม + โพสต์ Discord
T-1 นาที  : "⚠ ... อีก 1 นาที"
T-10..1 ว.: "🔄 รีสตาร์ทใน N..." (ทุกวินาที)
T-0       : "💾 กำลังบันทึกโลก..." → SaveManager.save() → "🔄 กำลังรีสตาร์ท!" → Provider.shutdown()
            → panel เด้งเซิร์ฟกลับ → plugin โพสต์ "🟢 ออนไลน์แล้ว — รอบถัดไป 06:00 (อีก 23h 59m)"
```

### รีฉุกเฉิน
| Discord | ในเกม (แอดมิน) | ผล |
|---------|----------------|-----|
| `/restart in 5` | `/srestart 5` | ตั้งรีในอีก 5 นาที (มี warn/countdown ตามช่วง) |
| `/restart now` | `/srestart now` | บันทึกโลกแล้วปิดทันที |
| `/restart cancel` | `/srestart cancel` | ยกเลิกรีฉุกเฉิน กลับไปตารางปกติ |

---

## ตาราง MySQL (prefix `sr_`)
| ตาราง | ทิศ | คอลัมน์หลัก |
|-------|-----|-------------|
| `sr_status` | เกม→บอท | `online, players, max_players, next_restart(UTC), state, embed_msg_id` |
| `sr_commands` | บอท→เกม | `command(restart/cancel), arg(นาที,0=now), requested_by` |
| `sr_events` | เกม→บอท | `kind, message, posted` |

ทั้งปลั๊กอินและบอทสร้างตาราง `IF NOT EXISTS` ตอนเริ่ม (รันก่อน/หลังกันก็ได้)

## Troubleshooting
- **เซิร์ฟดับไม่กลับมา** → เปิด auto-restart/keep-online ใน panel
- **คำสั่งไม่ถูกล็อก** → ดู log `[ScheduledRestart] Harmony patch FAILED`; เช็คว่ามี `0Harmony.dll` ใน `Rocket/Libraries/`
- **embed ไม่ขึ้น/Offline ตลอด** → เช็ค `STATUS_CHANNEL_ID`, สิทธิ์บอทในห้อง, และว่า connection string ของ
  ปลั๊กอิน+บอทชี้ DB เดียวกัน (`SR_PREFIX` ต้องตรง `Database.TablePrefix`)
- **เวลาเพี้ยน** → `TimezoneOffsetHours` (plugin) และ `TZ_OFFSET_HOURS` (bot) ต้อง = 7

Built by imaximum.tech.
