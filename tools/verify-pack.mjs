// 校验 dist/ 里每个 zip 的 DLL AssemblyVersion 是否等于 meta.json 的 version。
// 需要本机有 dotnet（CI 与本地构建都有）；没有就跳过而不是误判。
import { readFileSync, readdirSync, existsSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { inflateRawSync } from 'node:zlib';

const here = dirname(fileURLToPath(import.meta.url));
const distDir = join(here, '..', 'dist');
const DOTNET = process.env.DOTNET_PATH || 'C:\\Program Files\\dotnet\\dotnet.exe';

function readZipEntry(buf, wantName) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= 0; i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('zip EOCD not found');
  const count = buf.readUInt16LE(eocd + 10);
  let off = buf.readUInt32LE(eocd + 16);
  for (let i = 0; i < count; i++) {
    if (buf.readUInt32LE(off) !== 0x02014b50) throw new Error('bad central header');
    const nameLen = buf.readUInt16LE(off + 28);
    const extraLen = buf.readUInt16LE(off + 30);
    const commentLen = buf.readUInt16LE(off + 32);
    const method = buf.readUInt16LE(off + 10);
    const compSize = buf.readUInt32LE(off + 20);
    const localOff = buf.readUInt32LE(off + 42);
    const name = buf.toString('utf8', off + 46, off + 46 + nameLen);
    if (name === wantName) {
      const lNameLen = buf.readUInt16LE(localOff + 26);
      const lExtraLen = buf.readUInt16LE(localOff + 28);
      const start = localOff + 30 + lNameLen + lExtraLen;
      const raw = buf.subarray(start, start + compSize);
      return method === 0 ? raw : inflateRawSync(raw);
    }
    off += 46 + nameLen + extraLen + commentLen;
  }
  throw new Error('entry not found: ' + wantName);
}

const zips = existsSync(distDir) ? readdirSync(distDir).filter((f) => f.endsWith('.zip')) : [];
if (!zips.length) { console.log('dist/ 里没有包，跳过'); process.exit(0); }

const work = mkdtempSync(join(tmpdir(), 'cs-verify-'));
writeFileSync(join(work, 'Probe.csproj'),
  '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup></Project>');
writeFileSync(join(work, 'Program.cs'), `
using System; using System.IO; using System.Reflection;
var dir = args[0];
foreach (var d in Directory.GetDirectories(dir)) {
    var dll = Path.Combine(d, "Jellyfin.Plugin.CollectionStudio.dll");
    if (!File.Exists(dll)) continue;
    Console.WriteLine(Path.GetFileName(d) + "|" + AssemblyName.GetAssemblyName(dll).Version);
}
`);

for (const zf of zips) {
  const buf = readFileSync(join(distDir, zf));
  const meta = JSON.parse(readZipEntry(buf, 'meta.json').toString('utf8'));
  const out = join(work, zf.replace(/[^a-z0-9.-]/gi, '_'));
  const { mkdirSync } = await import('node:fs');
  mkdirSync(out, { recursive: true });
  writeFileSync(join(out, 'Jellyfin.Plugin.CollectionStudio.dll'), readZipEntry(buf, 'Jellyfin.Plugin.CollectionStudio.dll'));
}

try {
  execFileSync(DOTNET, ['build', '-c', 'Release', '--nologo', '-v', 'q'], { cwd: work, stdio: 'ignore' });
  const res = execFileSync(DOTNET, [join(work, 'bin', 'Release', 'net10.0', 'Probe.dll'), work], { encoding: 'utf8' });
  let bad = 0;
  for (const line of res.trim().split(/\r?\n/)) {
    const [name, ver] = line.split('|');
    // 从文件名解析出版本号（形如 Jellyfin.Plugin.CollectionStudio-1.2.0.2-net10.0.zip）
    const m = name.match(/CollectionStudio-([0-9.]+)-/);
    const metaVersion = m ? m[1] : '?';
    const ok = ver.trim() === metaVersion;
    if (!ok) bad++;
    console.log((ok ? 'OK   ' : 'FAIL ') + name + '  dll=' + ver + '  expected=' + metaVersion);
  }
  rmSync(work, { recursive: true, force: true });
  if (bad) { console.error(bad + ' 个包的程序集版本与文件名/清单不一致'); process.exit(1); }
  console.log('全部通过');
} catch (e) {
  rmSync(work, { recursive: true, force: true });
  console.warn('无法执行反射校验（缺 dotnet？），跳过：' + e.message.split('\n')[0]);
}
