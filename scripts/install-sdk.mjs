import { createWriteStream, existsSync, mkdirSync, readFileSync } from 'node:fs';
import { pipeline } from 'node:stream/promises';
import { Readable } from 'node:stream';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dir=path.join(root,'.tools','dotnet');
if(existsSync(path.join(dir,'dotnet.exe'))) { console.log('Portable SDK already present.'); process.exit(0); }
mkdirSync(dir,{recursive:true});
const url='https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip';
const sha='9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366';
const zip=path.join(root,'.tools','dotnet-sdk-10.0.400-win-x64.zip');
if(!existsSync(zip)) {
  console.log('Downloading official .NET 10.0.400 portable SDK…');
  const response=await fetch(url);
  if(!response.ok) throw new Error(`Download failed: ${response.status}`);
  await pipeline(Readable.fromWeb(response.body),createWriteStream(zip));
}
if(createHash('sha512').update(readFileSync(zip)).digest('hex')!==sha) throw new Error('SDK SHA-512 mismatch.');
console.log('SHA-512 verified; extracting…');
const r=spawnSync('tar.exe',['-xf',zip,'-C',dir],{stdio:'inherit',windowsHide:true});
if(r.status!==0)process.exit(r.status||1);
console.log('Portable SDK ready.');
