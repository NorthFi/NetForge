// root/selfhost/web/network-changes.js
'use strict';
async function networkChangesConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Network change control</h1><p class="sub">Immutable scripts · independent approval · manual or agent execution</p></div><button id="nc-refresh">Refresh</button></div><p class="note">Manual handoff or explicit queued RouterOS agent execution. Agent results are transport evidence only. Verify the actual device outcome before further handoffs.</p><div id="nc-main"></div><div id="nc-detail"></div>');
 const main=el.querySelector('#nc-main'),detail=el.querySelector('#nc-detail');
 let scripts=[],groups=[],profiles=[],plans=[],selected=null,seq=0;
 const fail=e=>{if(main.isConnected)notice(e.message,true)};
 const option=(value,label)=>'<option value="'+esc(value)+'">'+esc(label)+'</option>';
 function form(title,fields,save){
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error" id="nc-error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{
   e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;
   try{await save(new FormData(e.target));e.target.reset();d.close();await load()}
   catch(err){d.querySelector('#nc-error').textContent=err.message;b.disabled=false}
  };
 }
 function showBody(title,r){
  const d=dialog('<h2>'+esc(title)+'</h2><p>SHA-256: '+esc(r.sha256)+'</p>'+(r.backupId?'<p>Verified backup ID: '+esc(r.backupId)+'</p>':'')+'<textarea readonly rows="16">'+esc(r.body)+'</textarea><p>Review only / manual execution. No command has been sent by NetForge.</p><button id="close">Close</button>');
  d.querySelector('#close').onclick=()=>d.close();
 }
 async function revisions(id){return api('/changes/scripts/'+id+'/versions')}
 async function library(s){
  const versions=await revisions(s.id);if(!main.isConnected)return;
  const d=dialog('<h2>'+esc(s.name)+'</h2><p>'+esc(s.description)+'</p><div class="buttons">'+(admin?'<button id="new-version">+ Revision</button>':'')+'<button id="close">Close</button></div><div class="tablewrap"><table><thead><tr><th>Revision</th><th>Hash</th><th>Created</th><th></th></tr></thead><tbody>'+versions.map(v=>'<tr><td>'+esc(v.revision)+'</td><td>'+esc(v.sha256.slice(0,16))+'…</td><td>'+esc(v.created_at)+'</td><td>'+(admin?'<button data-review="'+esc(v.id)+'">Review</button>':'')+'</td></tr>').join('')+'</tbody></table></div>');
  d.querySelector('#close').onclick=()=>d.close();
  d.querySelectorAll('[data-review]').forEach(b=>b.onclick=async()=>{try{showBody('Review immutable revision',await api('/changes/versions/'+b.dataset.review+'/review'))}catch(e){fail(e)}});
  if(admin)d.querySelector('#new-version').onclick=()=>{d.close();form('New script revision','<label>Script body (64 KiB maximum)</label><textarea name="body" required rows="14" maxlength="65536"></textarea><p>Do not embed passwords, tokens or private keys. This revision cannot be changed.</p>',f=>api('/changes/scripts/'+s.id+'/versions','POST',{body:f.get('body'),confirm:true}))};
 }
 function groupForm(g){
  const options=profiles.map(p=>'<option value="'+esc(p.id)+'" '+(g?.profile_ids.includes(p.id)?'selected':'')+'>'+esc(p.name)+' · '+esc(p.vendor)+'</option>').join('');
  form(g?'Edit group':'Create group',(g?'':'<label>Name</label><input name="name" maxlength="160" required>')+'<label>Backup profiles (1–50, Ctrl/Cmd to select multiple)</label><select name="profiles" multiple size="8" required>'+options+'</select><p>Existing plans retain their original target list.</p>',f=>g?api('/changes/groups/'+g.id,'PUT',{profileIds:f.getAll('profiles'),confirm:true}):api('/changes/groups','POST',{name:f.get('name'),profileIds:f.getAll('profiles')}));
 }
 async function planForm(){
  if(!scripts.length||!groups.length)throw Error('Create scripts, revisions and groups first');
  const d=dialog('<h2>Create maintenance plan</h2><form><label>Script</label><select name="script">'+scripts.map(s=>option(s.id,s.name+' · '+s.vendor)).join('')+'</select><label>Immutable revision</label><select name="versionId" required></select><label>Device group</label><select name="groupId">'+groups.map(g=>option(g.id,g.name)).join('')+'</select><label>Canary profile (first target)</label><select name="canaryProfileId" required></select><label>Reason</label><textarea name="reason" minlength="5" maxlength="500" required></textarea><label>Window start (browser local time)</label><input name="start" type="datetime-local" required><label>Window end (browser local time)</label><input name="end" type="datetime-local" required><p class="error" id="error"></p><button class="primary">Create pending plan</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  const f=d.querySelector('form'),script=f.elements.script,version=f.elements.versionId,group=f.elements.groupId,canary=f.elements.canaryProfileId;
  let versionSeq=0;
  const updateVersions=async()=>{const s=++versionSeq;version.innerHTML='';const vs=await revisions(script.value);if(s===versionSeq&&d.isConnected)version.innerHTML=vs.map(v=>option(v.id,'Revision '+v.revision)).join('')};
  const updateCanary=()=>{const g=groups.find(x=>x.id===group.value);canary.innerHTML=profiles.filter(p=>g.profile_ids.includes(p.id)).map(p=>option(p.id,p.name)).join('')};
  script.onchange=()=>updateVersions().catch(fail);group.onchange=updateCanary;updateCanary();await updateVersions();
  f.onsubmit=async e=>{
   e.preventDefault();const btn=f.querySelector('.primary');btn.disabled=true;
   try{await api('/changes/plans','POST',{versionId:version.value,groupId:group.value,canaryProfileId:canary.value,reason:f.elements.reason.value,windowStart:new Date(f.elements.start.value).toISOString(),windowEnd:new Date(f.elements.end.value).toISOString(),confirm:true});d.close();await load()}
   catch(err){d.querySelector('#error').textContent=err.message;btn.disabled=false}
  };
 }
 async function show(){
  const p=selected,n=++seq,path='/changes/plans/'+p.id;
  const targets=await api(path+'/targets');if(n!==seq||!main.isConnected)return;
  detail.innerHTML='<div class="toolbar topgap"><div><h2>'+esc(p.script)+' · revision '+esc(p.revision)+'</h2><p>'+esc(p.reason)+'</p><p class="sub">'+esc(p.state)+' · '+esc(p.window_start)+' – '+esc(p.window_end)+' · requested by '+esc(p.creator)+' · approved by '+esc(p.approver||'—')+'</p></div>'+(admin?'<div class="buttons"><button id="nc-review">Review script</button>'+(p.state==='pending'?'<button id="nc-approve">Approve</button>':'')+(['pending','approved','running'].includes(p.state)?'<button id="nc-cancel">Cancel plan</button>':'')+'</div>':'')+'</div><div class="card tablewrap"><table><thead><tr><th>Order</th><th>Target</th><th>State</th><th>Backup</th><th>Operator / outcome</th><th></th></tr></thead><tbody>'+targets.map(t=>'<tr><td>'+esc(t.ordinal)+(t.ordinal===1?' · canary':'')+'</td><td>'+esc(t.name)+'</td><td>'+esc(t.state)+(t.execution_state?' · '+esc(t.execution_state)+' / '+esc(t.execution_result||'—'):'')+'</td><td>'+esc(t.backup_id||'—')+'</td><td>'+esc(t.operator||'')+' '+esc(t.outcome_note||'')+'</td><td>'+(admin&&t.state==='pending'&&['approved','running'].includes(p.state)?'<button data-handoff="'+esc(t.id)+'">Handoff</button>'+(t.agent_enabled&&t.vendor==='routeros'?'<button data-queue="'+esc(t.id)+'">Queue agent</button>':'')+'':admin&&t.state==='handed_off'?'<button data-outcome="'+esc(t.id)+'">Record outcome</button><button data-abandon="'+esc(t.id)+'">Resolve uncertain</button>':'')+'</td></tr>').join('')+'</tbody></table></div>';
  if(!admin)return;
  detail.querySelector('#nc-review').onclick=async()=>{try{showBody('Review plan script',await api('/changes/versions/'+p.version_id+'/review'))}catch(e){fail(e)}};
  const approve=detail.querySelector('#nc-approve');if(approve)approve.onclick=()=>{if(confirm('Independently reviewed this exact revision, targets and window?'))api(path+'/approve','POST',{confirm:true}).then(load).catch(fail)};
  const cancel=detail.querySelector('#nc-cancel');if(cancel)cancel.onclick=()=>{if(confirm('Cancel remaining handoffs? Already exported content cannot be recalled.'))api(path+'/cancel','POST',{confirm:true}).then(load).catch(fail)};
  detail.querySelectorAll('[data-handoff]').forEach(b=>b.onclick=async()=>{
   if(!confirm('Confirm exact device identity, backup and rollback procedure. NetForge will reveal the script, not execute it.'))return;
   b.disabled=true;
   try{const r=await api(path+'/targets/'+b.dataset.handoff+'/handoff','POST',{confirm:true});showBody('Manual execution handoff',r);await load()}catch(e){fail(e);b.disabled=false}
  });
  detail.querySelectorAll('[data-queue]').forEach(b=>b.onclick=async()=>{
   if(!confirm('Queue this approved single-line command for the device-bound SSH agent? This may change live equipment. Verify rollback and local hash allowlist first.'))return;
   b.disabled=true;
   try{await api(path+'/targets/'+b.dataset.queue+'/handoff','POST',{confirm:true,automatic:true});await load();notice('Queued; run the agent within five minutes.')}catch(e){fail(e);b.disabled=false}
  });
  detail.querySelectorAll('[data-abandon]').forEach(b=>b.onclick=()=>form('Resolve uncertain handoff','<p>Only another administrator may resolve this handoff as uncertain. This halts the plan and cannot establish that the device is safe. Verify device state before future changes.</p><label>Incident / reason (no secrets)</label><textarea name="note" required minlength="5" maxlength="500"></textarea>',f=>api(path+'/targets/'+b.dataset.abandon+'/abandon','POST',{success:false,note:f.get('note'),confirm:true})));
  detail.querySelectorAll('[data-outcome]').forEach(b=>b.onclick=()=>form('Record actual execution result','<label>Result</label><select name="result"><option value="success">Succeeded, verified by operator</option><option value="failure">Failed / uncertain (halt plan)</option></select><label>Verification note (no secrets)</label><textarea name="note" minlength="5" maxlength="500" required></textarea>',f=>api(path+'/targets/'+b.dataset.outcome+'/outcome','POST',{success:f.get('result')==='success',note:f.get('note'),confirm:true})));
 }
 async function load(){
  [scripts,groups,profiles,plans]=await Promise.all([api('/changes/scripts'),api('/changes/groups'),api('/backups/profiles'),api('/changes/plans')]);
  if(!main.isConnected)return;
  main.innerHTML='<div class="toolbar"><h2>Script library</h2>'+(admin?'<button id="nc-script">+ Script</button>':'')+'</div><div class="card tablewrap"><table><tbody>'+scripts.map(s=>'<tr><td>'+esc(s.name)+'</td><td>'+esc(s.vendor)+'</td><td>'+esc(s.revisions)+' revisions</td><td><button data-script="'+esc(s.id)+'">Versions</button></td></tr>').join('')+'</tbody></table></div><div class="toolbar topgap"><h2>Device groups</h2>'+(admin?'<button id="nc-group">+ Group</button>':'')+'</div><div class="card tablewrap"><table><tbody>'+groups.map(g=>'<tr><td>'+esc(g.name)+'</td><td>'+esc(g.profile_ids.length)+' devices</td><td>'+(admin?'<button data-group="'+esc(g.id)+'">Edit</button>':'')+'</td></tr>').join('')+'</tbody></table></div><div class="toolbar topgap"><h2>Maintenance plans</h2>'+(admin?'<button class="primary" id="nc-plan">+ Plan</button>':'')+'</div><div class="card tablewrap"><table><thead><tr><th>Script</th><th>State</th><th>Window</th><th>Reason</th><th></th></tr></thead><tbody>'+plans.map(p=>'<tr><td>'+esc(p.script)+' r'+esc(p.revision)+'</td><td>'+esc(p.state)+'</td><td>'+esc(p.window_start)+'</td><td>'+esc(p.reason)+'</td><td><button data-plan="'+esc(p.id)+'">Inspect</button></td></tr>').join('')+'</tbody></table></div>';
  if(admin){
   const section=document.createElement('div');
   section.innerHTML='<div class="toolbar topgap"><h2>RouterOS execution agents</h2><button id="nc-agent">Enroll / rotate / revoke</button></div><p class="sub">A separate token and locally pinned SSH identity are required. Tokens never authorize downloads.</p><div id="nc-agents"></div>';
   main.prepend(section);
   const agents=await api('/changes/agents');
   if(!section.isConnected)return;
   section.querySelector('#nc-agents').innerHTML='<div class="card tablewrap"><table><tbody>'+agents.map(a=>'<tr><td>'+esc(a.name)+'</td><td>'+esc(a.address)+'</td><td>'+esc(a.enabled?'Enabled':'Disabled')+'</td></tr>').join('')+'</tbody></table></div>';
   section.querySelector('#nc-agent').onclick=()=>{
    const routers=profiles.filter(p=>p.vendor==='routeros');
    form('Manage execution agent','<label>RouterOS backup profile</label><select name="profile" required>'+routers.map(p=>option(p.id,p.name)).join('')+'</select><label>Action</label><select name="action"><option value="enable">Issue / rotate token</option><option value="revoke">Revoke token</option></select><p>Confirm only for a device and collector you administer.</p>',async f=>{
     const r=await api('/changes/agents/'+f.get('profile')+'/token','POST',{enable:f.get('action')==='enable',confirm:true});
     if(r.token){const d=dialog('<h2>Execution token (shown once)</h2><p>Profile: '+esc(f.get('profile'))+'</p><textarea readonly rows="3">'+esc(r.token)+'</textarea><button id="close">Close</button>');d.querySelector('#close').onclick=()=>d.close()}
    });
   };
  }
  main.querySelectorAll('[data-script]').forEach(b=>b.onclick=()=>library(scripts.find(s=>s.id===b.dataset.script)).catch(fail));
  main.querySelectorAll('[data-group]').forEach(b=>b.onclick=()=>groupForm(groups.find(g=>g.id===b.dataset.group)));
  main.querySelectorAll('[data-plan]').forEach(b=>b.onclick=()=>{selected=plans.find(p=>p.id===b.dataset.plan);show().catch(fail)});
  if(admin){
   main.querySelector('#nc-script').onclick=()=>form('Create script','<label>Name</label><input name="name" required maxlength="160"><label>Vendor</label><select name="vendor">'+['routeros','cambium','ubiquiti','mimosa','other'].map(v=>option(v,v)).join('')+'</select><label>Description</label><textarea name="description" maxlength="500"></textarea>',f=>api('/changes/scripts','POST',Object.fromEntries(f)));
   main.querySelector('#nc-group').onclick=()=>groupForm(null);
   main.querySelector('#nc-plan').onclick=()=>planForm().catch(fail);
  }
  if(selected){selected=plans.find(p=>p.id===selected.id)||null;if(selected)await show()}
 }
 el.querySelector('#nc-refresh').onclick=()=>load().catch(fail);
 try{await load()}catch(e){fail(e)}
}
