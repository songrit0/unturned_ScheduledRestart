# ⏰ ScheduledRestart

ปลั๊กอิน Unturned (RocketMod) **รีสตาร์ทเซิร์ฟเวอร์ตามเวลาอัตโนมัติ** + คุมผ่าน **Discord**

- 🕕 รีรายวันเวลาตายตัว — ค่าเริ่มต้น **06:00 น. (เวลาไทย)**
- 📢 เตือนในเกม **ก่อน 10 นาที / 1 นาที** + นับถอยหลัง **10→1 วินาที**
- 💾 **เซฟโลกก่อนปิดเสมอ** ของไม่หาย
- ⛔ **ล็อกคำสั่ง**ช่วง 10 นาทีสุดท้าย (กันคนใช้ /home /tpa หนีตาย) — แอดมินใช้ได้ปกติ
- 🟢 Discord โชว์ **`ผู้เล่น 3/100` + เวลารีรอบหน้า** อัปเดตเอง + สั่ง **รีฉุกเฉิน**ได้

---

## 🚀 เริ่มใช้งาน (3 ขั้น)

### 1️⃣ เอาปลั๊กอินขึ้นเซิร์ฟ
1. โหลดไฟล์ **[`dist/ScheduledRestart.dll`](dist/ScheduledRestart.dll)**
2. อัปไปไว้ที่ `Rocket/Plugins/` บนเซิร์ฟเวอร์ (เช่นในหน้า RestoreMonarchy panel)
3. รีสตาร์ทเซิร์ฟ 1 ครั้ง → ปลั๊กอินจะสร้างไฟล์ config ให้

> ✅ ปลั๊กอินต้องการ `0Harmony.dll` กับ `MySql.Data.dll` ใน `Rocket/Libraries/` — ปกติมีอยู่แล้วถ้าใช้ AutoCleanup/SellVault อยู่ (ถ้าไม่มี ก๊อปจาก `ScheduledRestart/lib/` ขึ้นไปด้วย)

### 2️⃣ ตั้งค่า (แก้แค่ 2 อย่าง)
เปิดไฟล์ `Rocket/Plugins/ScheduledRestart/ScheduledRestart.configuration.xml` แล้วแก้:

```xml
<!-- เวลารีสตาร์ท (เพิ่มได้หลายรอบ) -->
<DailyRestartTimes>
  <Time>06:00</Time>
  <!-- <Time>18:00</Time> -->
</DailyRestartTimes>

<!-- ต่อ MySQL ตัวเดียวกับร้านค้า (SellVault) -->
<Database>
  <ConnectionString>SERVER=localhost;DATABASE=unturned;UID=root;PASSWORD=123456</ConnectionString>
  <TablePrefix>sr_</TablePrefix>
</Database>
```

### 3️⃣ ⚠️ เปิด Auto Restart ใน panel
ปลั๊กอินแค่ **สั่งปิด**เซิร์ฟ — ตัว panel เป็นคนเปิดกลับ
→ ในหน้า **RestoreMonarchy panel เปิด "Auto Restart / Keep Online" ไว้**
(ไม่งั้นพอถึงเวลารี เซิร์ฟจะดับค้างไม่กลับมา)

**เท่านี้ก็รีอัตโนมัติ 06:00 ทุกวันแล้ว** 🎉 — ส่วน Discord อ่านต่อข้างล่าง (จะทำหรือไม่ก็ได้)

---

## 💬 ต่อ Discord (ไม่บังคับ)

ให้ Discord โชว์สถานะเซิร์ฟ + สั่งรีฉุกเฉินได้ ใช้ MySQL เดียวกับปลั๊กอิน

**วิธีง่ายสุด — รันแยกเป็นบอทของตัวเอง:**
```bash
cd DiscordBot
pip install -r requirements.txt
copy .env.example .env       # แก้ token / channel / รหัส MySQL ในไฟล์ .env
python standalone_bot.py
```

ใน `.env` กรอกแค่นี้พอ:
| ค่า | คือ |
|-----|-----|
| `DISCORD_TOKEN` | โทเคนบอท |
| `GUILD_ID` | id เซิร์ฟ Discord |
| `ADMIN_ROLE_ID` | role ที่สั่งรีได้ |
| `STATUS_CHANNEL_ID` | ห้องที่จะโชว์สถานะ + ประกาศ |
| `DB_*` | MySQL เดียวกับเซิร์ฟเกม |

> มีบอทเศรษฐกิจ (SellVault) อยู่แล้ว? เสียบรวมได้ ไม่ต้องรันสองบอท — เอา 3 ไฟล์ `restart_cog.py`, `restart_db.py`, `restart_config.py` ไปไว้ด้วยกัน แล้วใน `bot.py` เพิ่ม `await bot.add_cog(RestartCog(bot))`

### คำสั่ง Discord
| คำสั่ง | ทำอะไร |
|--------|--------|
| `/status` | ดูสถานะเซิร์ฟ (ผู้เล่น + เวลารีรอบหน้า) |
| `/restart in <นาที>` | สั่งรีในอีกกี่นาที (มี countdown ในเกม) |
| `/restart now` | เซฟแล้วรีทันที |
| `/restart cancel` | ยกเลิกการรีฉุกเฉิน |

> สั่งในเกมก็ได้ (แอดมิน): `/srestart 5` · `/srestart now` · `/srestart cancel`

---

## 🔄 ตอนรีสตาร์ทจะเป็นแบบนี้
```
ก่อน 10 นาที : "⚠ เซิร์ฟจะรีสตาร์ทในอีก 10 นาที"   + เริ่มล็อกคำสั่ง
ก่อน 1 นาที  : "⚠ ... อีก 1 นาที"
10 วิสุดท้าย : "🔄 รีสตาร์ทใน 10... 9... 8..."
ถึงเวลา      : เซฟโลก → ปิดเซิร์ฟ → panel เปิดกลับ → ประกาศ "🟢 ออนไลน์แล้ว รอบหน้า 06:00"
```

---

## 🔧 อยากปรับเพิ่ม?
แก้ในไฟล์ `ScheduledRestart.configuration.xml`:

| ค่า | ความหมาย | ค่าเริ่มต้น |
|-----|----------|-------------|
| `DailyRestartTimes` | เวลารี (ไทย, ใส่หลายอันได้) | `06:00` |
| `WarnBeforeSeconds` | เตือนล่วงหน้ากี่วินาที | `600`, `60` |
| `CountdownSeconds` | นับถอยหลังกี่วินาทีท้าย | `10` |
| `LockdownSeconds` | ล็อกคำสั่งกี่วินาทีก่อนรี | `600` |
| `LockdownEnabled` | เปิด/ปิดการล็อกคำสั่ง | `true` |
| `TimezoneOffsetHours` | โซนเวลา (ไทย=7) | `7` |

> แก้ config แล้วต้อง `/rocket reload ScheduledRestart` หรือรีเซิร์ฟ ถึงจะมีผล

---

## ❓ แก้ปัญหา
| อาการ | แก้ |
|-------|-----|
| เซิร์ฟดับแล้วไม่กลับมา | เปิด **Auto Restart** ใน panel |
| คำสั่งไม่ถูกล็อก | เช็คว่ามี `0Harmony.dll` ใน `Rocket/Libraries/` (ดู log `[ScheduledRestart] Command-lockdown patch applied`) |
| Discord ขึ้น Offline ตลอด | เช็ค `STATUS_CHANNEL_ID`, สิทธิ์บอทในห้อง, และ `ConnectionString` + `SR_PREFIX` ต้องตรงกับปลั๊กอิน |
| เวลาเพี้ยน | `TimezoneOffsetHours` (ปลั๊กอิน) กับ `TZ_OFFSET_HOURS` (บอท) ต้อง = 7 |

---

## 🛠️ สำหรับคนอยากแก้โค้ดเอง
```powershell
# build บน Windows ที่ลง Unturned + VS2022 (แก้ path เกมบนหัว build.ps1 ถ้าไม่ใช่ D:\SteamLibrary)
powershell -ExecutionPolicy Bypass -File ScheduledRestart\build.ps1
# -> ScheduledRestart\bin\ScheduledRestart.dll
```

**ทำงานยังไง:** ปลั๊กอิน (C#) กับบอท (Python) ไม่ได้คุยกันตรงๆ แต่ผ่าน **MySQL 3 ตาราง** —
`sr_status` (เกมเขียนสถานะ→บอทอ่าน), `sr_commands` (บอทสั่ง→เกมอ่าน), `sr_events` (เกมประกาศ→บอทโพสต์)
สร้างอัตโนมัติตอนเริ่ม

Built by imaximum.tech
