// root/selfhost/web/backups.js
'use strict';
async function backupConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Configuration backup vault</h1><p class="sub">Encrypted multi-vendor file history</p></div><div class="buttons"><button id="bv-refresh">Refresh</button>'+(admin?'<button id="bv-add" class="primary">+ Profile</button>':'')+'</div></div><p class="note">Files are never executed. A stored backup is not a verified restore. Only administrators may read file contents, compare or upload.</p><div id="bv-list"></div><div id="bv-detail"></div>');
 const list=el.querySelector('#bv-list'),detail=el.querySelector('#bv-detail');
 let selected=null,seq=0;
 const fail=e=>{if(list.isConnected)notice(e.message,true)};
 function form(title,fields,save){
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error" id="bv-error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{
   e.preventDefault();const btn=e.target.querySelector('.primary');btn.disabled=true;
   try{await save(new FormData(e.target));e.target.reset();d.close();await load()}
   catch(err){d.querySelector('#bv-error').textContent=err.message;btn.disabled=false}
  };
 }
 async function load(){
  const profiles=await api('/backups/profiles');if(!list.isConnected)return;
  list.innerHTML='<div class="card tablewrap"><table><thead><tr><th>Device</th><th>Vendor</th><th>Versions</th><th>Storage (bytes)</th><th>Collector</th><th>Last received</th><th></th></tr></thead><tbody>'+profiles.map(p=>'<tr><td>'+esc(p.name)+'</td><td>'+esc(p.vendor)+'</td><td>'+esc(p.versions)+'</td><td>'+esc(p.bytes)+'</td><td>'+esc(p.collector_enabled?'Enabled':'Disabled')+'</td><td>'+esc(p.last_received_at||'Never')+'</td><td><button data-profile="'+esc(p.id)+'">Versions</button></td></tr>').join('')+'</tbody></table>'+(profiles.length?'':'<p>No profiles configured.</p>')+'</div>';
  list.querySelectorAll('[data-profile]').forEach(b=>b.onclick=()=>{selected=profiles.find(p=>p.id===b.dataset.profile);show().catch(fail)});
  if(selected){selected=profiles.find(p=>p.id===selected.id)||null;if(selected)await show()}
 }
 async function enroll(){
  const [nas,equipment]=await Promise.all([api('/radius/nas-management'),api('/equipment')]);
  if(!list.isConnected)return;
  const options=nas.map(n=>'<option value="n:'+esc(n.id)+'">'+esc(n.shortname)+'</option>').concat(equipment.map(e=>'<option value="e:'+esc(e.id)+'">'+esc(e.name)+'</option>')).join('');
  form('Create backup profile','<label>Inventory asset</label><select name="source" required><option value="">Select</option>'+options+'</select><label>Name</label><input name="name" required maxlength="160"><label>Vendor</label><select name="vendor"><option value="routeros">MikroTik</option><option value="cambium">Cambium</option><option value="ubiquiti">Ubiquiti</option><option value="mimosa">Mimosa</option><option value="other">Other</option></select>',f=>{
   const [type,id]=f.get('source').split(':');
   return api('/backups/profiles','POST',{nasId:type==='n'?Number(id):null,equipmentId:type==='e'?id:null,name:f.get('name'),vendor:f.get('vendor'),confirm:true});
  });
 }
 async function show(){
  const p=selected,n=++seq,path='/backups/profiles/'+p.id;
  const versions=await api(path+'/versions');if(!list.isConnected||seq!==n)return;
  detail.innerHTML='<div class="toolbar topgap"><div><h2>'+esc(p.name)+'</h2><p class="sub">'+versions.length+' / 100 versions · 4 MiB maximum per file</p></div>'+(admin?'<div class="buttons"><button id="bv-token">Issue / rotate token</button><button id="bv-revoke">Revoke token</button><button id="bv-upload" class="primary">Upload file</button></div>':'')+'</div><div class="card tablewrap"><table><thead><tr><th>Filename</th><th>Kind</th><th>Bytes</th><th>SHA-256</th><th>Source</th><th>Created</th>'+(admin?'<th></th>':'')+'</tr></thead><tbody>'+versions.map(v=>'<tr><td>'+esc(v.filename)+'</td><td>'+esc(v.kind)+'</td><td>'+esc(v.size_bytes)+'</td><td title="'+esc(v.sha256)+'">'+esc(v.sha256.slice(0,16))+'…</td><td>'+esc(v.source)+'</td><td>'+esc(v.created_at)+'</td>'+(admin?'<td><a href="/api'+path+'/versions/'+esc(v.id)+'/download">Download</a></td>':'')+'</tr>').join('')+'</tbody></table></div><div id="bv-compare"></div>';
  if(!admin)return;
  detail.querySelector('#bv-token').onclick=async()=>{
   if(!confirm('Issue a new upload-only token? Previous token stops working.'))return;
   try{const r=await api(path+'/token','POST',{enable:true,confirm:true});
    const d=dialog('<h2>Collector token (shown once)</h2><p>Store securely. It permits uploads to this profile only.</p><p>Profile: '+esc(p.id)+'</p><textarea readonly rows="3">'+esc(r.token)+'</textarea><button id="close">Close</button>');
    d.querySelector('#close').onclick=()=>{d.querySelector('textarea').value='';d.close()};await load();
   }catch(e){fail(e)}
  };
  detail.querySelector('#bv-revoke').onclick=()=>{if(confirm('Revoke collector access?'))api(path+'/token','POST',{enable:false,confirm:true}).then(load).catch(fail)};
  detail.querySelector('#bv-upload').onclick=()=>form('Upload backup','<label>File (maximum 4 MiB)</label><input name="file" type="file" required><label>Format</label><select name="kind"><option value="text">UTF-8 text configuration</option><option value="binary">Binary / opaque vendor backup</option></select><p>Confirm this file belongs to the selected device.</p>',async f=>{
   const file=f.get('file');if(!file.size||file.size>4194304)throw Error('File must be 1–4194304 bytes');
   const query=new URLSearchParams({filename:file.name,kind:f.get('kind'),confirm:'true'});
   const r=await fetch('/api'+path+'/upload?'+query,{method:'POST',headers:{'Content-Type':'application/octet-stream','X-CSRF-Token':csrf},body:file,credentials:'same-origin'});
   if(!r.ok){let err;try{err=(await r.json()).error}catch{}throw Error(err||'Upload failed ('+r.status+')')}
  });
  const texts=versions.filter(v=>v.kind==='text');
  if(texts.length<2)return;
  const opts=texts.map(v=>'<option value="'+esc(v.id)+'">'+esc(v.created_at)+' · '+esc(v.filename)+'</option>').join('');
  const compare=detail.querySelector('#bv-compare');
  compare.innerHTML='<div class="card topgap"><h3>Compare text versions</h3><label>Older</label><select id="bv-older">'+opts+'</select><label>Newer</label><select id="bv-newer">'+opts+'</select><button id="bv-diff">Compare</button><div id="bv-result"></div></div>';
  compare.querySelector('#bv-older').selectedIndex=1;
  compare.querySelector('#bv-diff').onclick=async()=>{
   try{const q=new URLSearchParams({older:compare.querySelector('#bv-older').value,newer:compare.querySelector('#bv-newer').value});
    const r=await api(path+'/compare?'+q);
    compare.querySelector('#bv-result').innerHTML='<p>Byte identical: '+esc(r.byteIdentical)+' · Unchanged leading lines: '+esc(r.unchangedPrefix)+' · Changed range: '+esc(r.olderChangedLines)+' → '+esc(r.newerChangedLines)+'</p><p class="sub">Contiguous changed range, not a minimal line diff. Preview limited to 200 lines per version and 1024 characters per line.</p><div class="grid"><div><h4>Older</h4><textarea readonly rows="12">'+esc(r.olderPreview.join('\n'))+'</textarea></div><div><h4>Newer</h4><textarea readonly rows="12">'+esc(r.newerPreview.join('\n'))+'</textarea></div></div>';
   }catch(e){fail(e)}
  };
 }
 el.querySelector('#bv-refresh').onclick=()=>load().catch(fail);
 if(admin)el.querySelector('#bv-add').onclick=()=>enroll().catch(fail);
 try{await load()}catch(e){fail(e)}
}
