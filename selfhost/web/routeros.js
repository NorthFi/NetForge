// root/selfhost/web/routeros.js
'use strict';
async function routerosConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>RouterOS management</h1><p class="sub">Pinned HTTPS · read-only device inventory</p></div><div class="buttons"><button id="ros-refresh">Refresh</button>'+(admin?'<button class="primary" id="ros-add">+ Enroll router</button>':'')+'</div></div><p class="note">Only enrolled, allowlisted devices can be inspected. No commands or configuration changes are sent. A failed inspection does not prove a router is down.</p><div id="ros-devices"></div><div id="ros-detail"></div>');
 const listArea=el.querySelector('#ros-devices'),detail=el.querySelector('#ros-detail');
 const fail=e=>{if(listArea.isConnected)notice(e.message,true)};
 let selected=null,sequence=0;
 async function load(){
  const devices=await api('/routeros/devices');
  if(!listArea.isConnected)return;
  listArea.innerHTML='<div class="card tablewrap"><table><thead><tr><th>Router</th><th>Management endpoint</th><th>Enabled</th><th>Last inspection</th><th>Result</th><th></th></tr></thead><tbody>'+devices.map(d=>'<tr><td>'+esc(d.name)+'</td><td>'+esc(d.address)+':'+esc(d.port)+'</td><td>'+esc(d.enabled?'Yes':'No')+'</td><td>'+esc(d.last_poll_at||'Never')+'</td><td>'+esc(d.last_status)+'</td><td><button data-device="'+esc(d.id)+'">Inspect</button></td></tr>').join('')+'</tbody></table>'+(devices.length?'':'<p>No enrolled routers.</p>')+'</div>';
  listArea.querySelectorAll('[data-device]').forEach(b=>b.onclick=()=>{selected=devices.find(d=>d.id===b.dataset.device);show().catch(fail)});
  if(selected){selected=devices.find(d=>d.id===selected.id)||null;if(selected)await show()}
 }
 const credentialFields='<label>RouterOS username</label><input name="username" autocomplete="off" required maxlength="80"><label>RouterOS password (12–256 characters)</label><input type="password" name="password" autocomplete="new-password" required minlength="12" maxlength="256"><label>Trusted SHA-256 certificate fingerprint (64 hex characters)</label><input name="certificateSha256" required pattern="[A-Fa-f0-9]{64}" maxlength="64">';
 function form(title,fields,submit){
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error" id="ros-error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{
   e.preventDefault();const b=e.target.querySelector('button.primary');b.disabled=true;
   try{await submit(Object.fromEntries(new FormData(e.target)));e.target.reset();d.close();await load()}
   catch(err){d.querySelector('#ros-error').textContent=err.message;b.disabled=false}
  };
 }
 async function enroll(){
  const [nas,equipment]=await Promise.all([api('/radius/nas-management'),api('/equipment')]);
  if(!listArea.isConnected)return;
  const options=nas.map(n=>'<option value="n:'+esc(n.id)+'">'+esc(n.shortname)+' · '+esc(n.nasname)+'</option>').concat(equipment.filter(e=>e.management_ip).map(e=>'<option value="e:'+esc(e.id)+'">'+esc(e.name)+' · '+esc(e.management_ip)+'</option>')).join('');
  form('Enroll RouterOS device','<label>Saved NAS or equipment</label><select name="source" required><option value="">Select device</option>'+options+'</select><label>Display name</label><input name="name" required maxlength="160"><label>HTTPS port</label><input name="port" type="number" min="1" max="65535" value="443" required>'+credentialFields+'<p>Verify the certificate fingerprint through a trusted channel. Use a dedicated read-only account. The device starts disabled.</p>',f=>{
   const [kind,id]=f.source.split(':');
   return api('/routeros/devices','POST',{nasId:kind==='n'?Number(id):null,equipmentId:kind==='e'?id:null,name:f.name,port:Number(f.port),username:f.username,password:f.password,certificateSha256:f.certificateSha256,confirm:true});
  });
 }
 async function show(){
  const current=selected,seq=++sequence;
  const d=await api('/routeros/devices/'+current.id);
  if(!listArea.isConnected||seq!==sequence)return;
  const snapshot=d.snapshot?JSON.parse(d.snapshot):{};
  detail.innerHTML='<div class="toolbar topgap"><div><h2>'+esc(d.name)+'</h2><p class="sub">'+esc(d.address)+':'+esc(d.port)+' · '+esc(d.last_status)+' · '+esc(d.last_poll_at||'Not inspected')+'</p></div>'+(admin?'<div class="buttons"><button id="ros-enable">'+(d.enabled?'Disable':'Enable')+'</button><button id="ros-rotate">Rotate credentials / pin</button><button id="ros-poll" class="primary" '+(d.enabled?'':'disabled')+'>Inspect now</button></div>':'')+'</div><div id="ros-sections"></div>';
  const sections=detail.querySelector('#ros-sections');
  Object.entries(snapshot).forEach(([name,section])=>{
   const rows=section.rows||[],keys=[...new Set(rows.flatMap(r=>Object.keys(r)))];
   sections.insertAdjacentHTML('beforeend','<details class="card topgap"><summary>'+esc(name)+' · '+esc(section.status)+' · '+rows.length+' records</summary>'+(keys.length?'<div class="tablewrap"><table><thead><tr>'+keys.map(k=>'<th>'+esc(k)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr>'+keys.map(k=>'<td>'+esc(r[k]??'—')+'</td>').join('')+'</tr>').join('')+'</tbody></table></div>':'<p>No returned records.</p>')+'</details>');
  });
  if(!Object.keys(snapshot).length)sections.textContent='No inspection data.';
  if(!admin)return;
  detail.querySelector('#ros-enable').onclick=()=>{if(confirm((d.enabled?'Disable ':'Enable ')+d.name+' for read-only inspection?'))api('/routeros/devices/'+d.id+'/enabled','POST',{enabled:!d.enabled,confirm:true}).then(load).catch(fail)};
  detail.querySelector('#ros-rotate').onclick=()=>form('Rotate credentials and trusted certificate',credentialFields+'<p>Rotation disables the device and clears its old snapshot.</p>',f=>api('/routeros/devices/'+d.id+'/credentials','POST',{...f,confirm:true}));
  detail.querySelector('#ros-poll').onclick=async e=>{
   if(!confirm('Run read-only HTTPS inspection of '+d.name+'?'))return;
   const b=e.currentTarget;b.disabled=true;b.textContent='Inspecting…';
   try{const r=await api('/routeros/devices/'+d.id+'/poll','POST',{confirm:true});await load();if(listArea.isConnected)notice('Inspection: '+r.status+' ('+r.sectionsOk+'/'+r.totalSections+' sections)')}
   catch(err){fail(err);b.disabled=false;b.textContent='Inspect now'}
  };
 }
 el.querySelector('#ros-refresh').onclick=()=>load().catch(fail);
 if(admin)el.querySelector('#ros-add').onclick=()=>enroll().catch(fail);
 try{await load()}catch(err){fail(err)}
}
