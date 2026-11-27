// root/selfhost/web/compliance.js
'use strict';
async function complianceConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Network compliance</h1><p class="sub">Configuration baselines · firmware policy · backup freshness</p></div><button id="cmp-refresh">Refresh</button></div><p class="note">Results are based on stored read-only inspections. Missing or stale evidence is unknown, not compliant. Automatic inspection is disabled until configured per router.</p><div class="buttons"><button id="cmp-prev">Previous</button><span id="cmp-page"></span><button id="cmp-next">Next</button></div><div id="cmp-list"></div><div id="cmp-detail"></div>');
 const list=el.querySelector('#cmp-list'),detail=el.querySelector('#cmp-detail');
 let offset=0,selected=null,seq=0;
 const fail=e=>{if(list.isConnected)notice(e.message,true)};
 const opt=(v,l)=>'<option value="'+esc(v)+'">'+esc(l)+'</option>';
 function form(title,fields,save){
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{
   e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;
   try{await save(new FormData(e.target));d.close();await load()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}
  };
 }
 async function policy(d){
  const backups=(await api('/backups/profiles')).filter(b=>b.vendor==='routeros');
  form('Compliance policy — '+d.name,'<label>Exact RouterOS version (optional)</label><input name="version" maxlength="32" value="'+esc(d.expected_version||'')+'"><label>Backup profile (same inventory asset, optional)</label><select name="backup"><option value="">Not checked</option>'+backups.map(b=>'<option value="'+esc(b.id)+'" '+(b.id===d.backup_profile_id?'selected':'')+'>'+esc(b.name)+'</option>').join('')+'</select><label>Maximum backup version age (hours)</label><input type="number" name="backupAge" min="1" max="168" required value="'+esc(d.backup_max_hours||24)+'"><label>Maximum inspection age (hours)</label><input type="number" name="snapshotAge" min="1" max="168" required value="'+esc(d.snapshot_max_hours||24)+'"><label>Require approved baseline</label><select name="baseline">'+(d.require_baseline===false?opt('false','No')+opt('true','Yes'):opt('true','Yes')+opt('false','No'))+'</select><label>Read-only inspection interval in minutes (0 = disabled; 15–1440)</label><input name="interval" type="number" min="0" max="1440" value="'+esc(d.poll_interval_minutes||0)+'" required><p>Automatic inspections use the existing pinned REST credentials and management allowlist.</p>',f=>api('/compliance/devices/'+d.id+'/policy','PUT',{expectedVersion:f.get('version'),backupProfileId:f.get('backup')||null,backupMaxHours:Number(f.get('backupAge')),snapshotMaxHours:Number(f.get('snapshotAge')),requireBaseline:f.get('baseline')==='true',pollIntervalMinutes:Number(f.get('interval')),confirm:true}));
 }
 async function show(){
  const d=selected,n=++seq,path='/compliance/devices/'+d.id;
  detail.innerHTML='<div class="toolbar topgap"><div><h2>'+esc(d.name)+'</h2><p class="sub">'+esc(d.address)+' · '+esc(d.highsite||'No linked highsite')+'</p></div>'+(admin?'<div class="buttons"><button id="cmp-policy">Policy</button>'+(d.configured?'<button id="cmp-poll">Inspect now</button><button id="cmp-baseline">Approve current baseline</button>':'')+'</div>':'')+'</div><div id="cmp-result"></div>';
  if(admin){
   detail.querySelector('#cmp-policy').onclick=()=>policy(d).catch(fail);
   const poll=detail.querySelector('#cmp-poll');if(poll)poll.onclick=async()=>{if(!confirm('Run read-only RouterOS inspection?'))return;poll.disabled=true;try{await api('/routeros/devices/'+d.id+'/poll','POST',{confirm:true});await load()}catch(e){fail(e);poll.disabled=false}};
   const base=detail.querySelector('#cmp-baseline');if(base)base.onclick=()=>form('Approve configuration baseline','<p>Review the latest inspection in RouterOS Management first. This records the current static configuration as the approved standard; prior baselines remain immutable.</p><label>Reason</label><textarea name="reason" required minlength="5" maxlength="240"></textarea>',f=>api(path+'/baseline','POST',{reason:f.get('reason'),confirm:true}));
  }
  const result=detail.querySelector('#cmp-result');
  if(!d.configured){result.textContent='No compliance policy configured.';return}
  const [evaluation,history]=await Promise.all([api(path),api(path+'/baselines')]);
  if(n!==seq||!result.isConnected)return;
  result.innerHTML='<div class="card"><h3>'+esc(evaluation.status)+'</h3><p class="sub">Inspection: '+esc(evaluation.lastPollAt||'Never')+' · Backup version: '+esc(evaluation.backupCreatedAt||'None')+'</p><div class="tablewrap"><table><thead><tr><th>Check</th><th>State</th><th>Evidence</th></tr></thead><tbody>'+evaluation.checks.map(c=>'<tr><td>'+esc(c.name)+'</td><td>'+esc(c.state)+'</td><td>'+esc(c.detail)+'</td></tr>').join('')+'</tbody></table></div></div><div class="card topgap"><h3>Baseline history</h3><div class="tablewrap"><table><thead><tr><th>Created</th><th>Author</th><th>Reason</th><th>SHA-256</th></tr></thead><tbody>'+history.map(h=>'<tr><td>'+esc(h.created_at)+'</td><td>'+esc(h.author)+'</td><td>'+esc(h.reason)+'</td><td title="'+esc(h.sha256)+'">'+esc(h.sha256.slice(0,16))+'…</td></tr>').join('')+'</tbody></table></div></div>';
 }
 async function load(){
  const rows=await api('/compliance/devices?offset='+offset);if(!list.isConnected)return;
  el.querySelector('#cmp-page').textContent='Records '+(rows.length?offset+1:offset)+'–'+(offset+rows.length);
  el.querySelector('#cmp-prev').disabled=offset===0;el.querySelector('#cmp-next').disabled=rows.length<100;
  list.innerHTML='<div class="card tablewrap"><table><thead><tr><th>Device</th><th>Highsite</th><th>Policy</th><th>Expected version</th><th>Inspection</th><th>Schedule</th><th></th></tr></thead><tbody>'+rows.map(d=>'<tr><td>'+esc(d.name)+'</td><td>'+esc(d.highsite||'—')+'</td><td>'+esc(d.configured?'Configured':'None')+'</td><td>'+esc(d.expected_version||'—')+'</td><td>'+esc(d.last_status)+' · '+esc(d.last_poll_at||'Never')+'</td><td>'+esc(d.poll_interval_minutes?d.poll_interval_minutes+' min':'Off')+'</td><td><button data-cmp="'+esc(d.id)+'">Inspect policy</button></td></tr>').join('')+'</tbody></table></div>';
  list.querySelectorAll('[data-cmp]').forEach(b=>b.onclick=()=>{selected=rows.find(d=>d.id===b.dataset.cmp);show().catch(fail)});
  if(selected){selected=rows.find(d=>d.id===selected.id)||null;if(selected)await show();else detail.innerHTML=''}
 }
 el.querySelector('#cmp-prev').onclick=()=>{offset=Math.max(0,offset-100);load().catch(fail)};
 el.querySelector('#cmp-next').onclick=()=>{offset+=100;load().catch(fail)};
 el.querySelector('#cmp-refresh').onclick=()=>load().catch(fail);
 try{await load()}catch(e){fail(e)}
}
