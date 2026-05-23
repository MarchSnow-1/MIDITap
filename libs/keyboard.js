const koffi = require('koffi');

// 通过 koffi 加载 Win32 动态库。
// - user32.dll: 提供 SendInput，用于模拟键盘输入
// - kernel32.dll: 提供 GetTickCount，用于填充输入事件时间戳
const user32 = koffi.load('user32.dll');
const kernel32 = koffi.load('kernel32.dll');

// 声明原生函数签名：
// UINT SendInput(UINT nInputs, LPINPUT pInputs, int cbSize)
const SendInput = user32.func('uint32 __stdcall SendInput(uint32 nInputs, uint8 *pInputs, int cbSize)');

// 获取系统启动后的毫秒计时，用于 INPUT.time 字段。
const GetTickCount = kernel32.func('uint32 __stdcall GetTickCount()');

// VK（Virtual-Key）键码表：配置文件中的按键名会映射到这里的数值。
// 说明：键码取值遵循 Windows 官方虚拟键定义。
const VK = {
  // 字母键
  'a':0x41,'b':0x42,'c':0x43,'d':0x44,'e':0x45,'f':0x46,'g':0x47,'h':0x48,
  'i':0x49,'j':0x4A,'k':0x4B,'l':0x4C,'m':0x4D,'n':0x4E,'o':0x4F,'p':0x50,
  'q':0x51,'r':0x52,'s':0x53,'t':0x54,'u':0x55,'v':0x56,'w':0x57,'x':0x58,
  'y':0x59,'z':0x5A,

  // 数字键（主键盘区）
  '0':0x30,'1':0x31,'2':0x32,'3':0x33,'4':0x34,
  '5':0x35,'6':0x36,'7':0x37,'8':0x38,'9':0x39,

  // 功能键
  'f1':0x70,'f2':0x71,'f3':0x72,'f4':0x73,'f5':0x74,'f6':0x75,
  'f7':0x76,'f8':0x77,'f9':0x78,'f10':0x79,'f11':0x7A,'f12':0x7B,
  'f13':0x7C,'f14':0x7D,'f15':0x7E,'f16':0x7F,'f17':0x80,'f18':0x81,
  'f19':0x82,'f20':0x83,'f21':0x84,'f22':0x85,'f23':0x86,'f24':0x87,

  // 控制键
  'backspace':0x08,'tab':0x09,'enter':0x0D,'shift':0x10,'ctrl':0x11,
  'alt':0x12,'pause':0x13,'capslock':0x14,'escape':0x1B,'esc':0x1B,'space':0x20,

  // 导航键
  'pageup':0x21,'pagedown':0x22,'end':0x23,'home':0x24,
  'left':0x25,'up':0x26,'right':0x27,'down':0x28,
  'insert':0x2D,'delete':0x2E,

  // 左右修饰键
  'lshift':0xA0,'rshift':0xA1,
  'lctrl':0xA2,'rctrl':0xA3,
  'lalt':0xA4,'ralt':0xA5,
  'lwin':0x5B,'rwin':0x5C,'win':0x5B,

  // 小键盘区
  'num0':0x60,'num1':0x61,'num2':0x62,'num3':0x63,'num4':0x64,
  'num5':0x65,'num6':0x66,'num7':0x67,'num8':0x68,'num9':0x69,
  'multiply':0x6A,
  'add':0x6B,
  'separator':0x6C,
  'subtract':0x6D,
  'decimal':0x6E,
  'divide':0x6F,
  'numlock':0x90,

  // 美式键盘标点
  'semicolon':0xBA,
  'equal':0xBB,
  'comma':0xBC,
  'minus':0xBD,
  'period':0xBE,
  'slash':0xBF,
  'backquote':0xC0,
  'lbracket':0xDB,
  'backslash':0xDC,
  'rbracket':0xDD,
  'quote':0xDE,

  // 系统键
  'printscreen':0x2C,'scrolllock':0x91,'apps':0x5D,

  // 媒体键
  'mute':0xAD,'volumedown':0xAE,'volumeup':0xAF,
  'nexttrack':0xB0,'prevtrack':0xB1,'stop':0xB2,'playpause':0xB3,

  // 其他
  'select':0x29,'print':0x2A,'execute':0x2B,'help':0x2F,
  'sleep':0x5F,
};

// 反向索引：VK -> keyName。
// 作用：日志输出时可以 O(1) 从 VK 反查按键名，避免每次线性扫描整个 VK 表。
const VK_NAME_BY_CODE = new Map();
for (const [keyName, vkCode] of Object.entries(VK)) {
  // 多个 keyName 可能映射到同一个 VK（别名场景）。
  // 保留第一次出现的名称，保证日志稳定。
  if (!VK_NAME_BY_CODE.has(vkCode)) {
    VK_NAME_BY_CODE.set(vkCode, keyName);
  }
}

// 构造一个 Win32 INPUT（Keyboard）结构体的二进制缓冲区。
// 当前按 64 位结构布局写入，长度为 40 字节。
// 关键字段：
// - type      (offset 0, 4 bytes)   : 1 表示键盘输入
// - wVk       (offset 8, 2 bytes)   : 虚拟键码
// - wScan     (offset 10, 2 bytes)  : 扫描码（这里不用，填 0）
// - dwFlags   (offset 12, 4 bytes)  : 事件标记（0=按下, 0x0002=抬起）
// - time      (offset 16, 4 bytes)  : 时间戳
function makeKeyBuffer(vkCode, flags) {
  const buf = Buffer.alloc(40);
  buf.writeUInt32LE(1); // type = INPUT_KEYBOARD
  buf.writeUInt16LE(vkCode, 8);
  buf.writeUInt16LE(0, 10);
  buf.writeUInt32LE(flags, 12);
  buf.writeUInt32LE(GetTickCount(), 16);
  return buf;
}

// 发送键盘事件。
// 参数：
// - vkCode: 目标键位（来自 VK 表）
// - flags: 0 表示按下，0x0002 表示抬起
function sendKey(vkCode, flags) {
  SendInput.async(1, makeKeyBuffer(vkCode, flags), 40, (err, ret) => {
    if (err) console.error('SendInput Error:', err);
    else if (ret === 0) console.error('SendInput Return 0');
  });
}

// 同步发送键盘事件（主要用于退出清理阶段）：
// - 退出时进程可能很快结束，异步调用存在来不及真正下发输入的风险
// - 因此在“全量抬键”场景下优先使用同步调用，提升释放成功率
function sendKeySync(vkCode, flags) {
  try {
    const ret = SendInput(1, makeKeyBuffer(vkCode, flags), 40);
    if (ret === 0) console.error('SendInput Return 0');
  } catch (err) {
    console.error('SendInput Error:', err);
  }
}

// VK 反查按键名（用于日志展示）。
function getKeyName(vkCode) {
  return VK_NAME_BY_CODE.get(vkCode);
}

module.exports = { VK, sendKey, sendKeySync, getKeyName };
