// root/selfhost/web/telemetry.js
// NetForge SNMPv3 and WISP highsite health views.
// All privileged requests are delegated to the existing CSRF-aware api() client.
'use strict';

function telemetryGraph(samples){
 const rows=samples.filter(x=>x.input_bps!==null && x.output_bps!==null &&
   Number.isFinite(Number(x.input_bps)) && Number.isFinite(Number(x.output_bps)));
 if(rows.length<2)return '<p class="muted">At least two valid consecutive counter samples are required for a throughput chart.</p>';
 const selected=rows.slice(-160);
 const width=760,height=230,left=60,right=15,top=18,bottom=40;
 const max=Math.max(1000,...selected.map(r=>Math.max(Number(r.input_bps),Number(r.output_bps))));
 const minTime=Date.parse(selected[0].observed_at);
 const maxTime=Date.parse(selected[selected.length-1].observed_at);
 const range=Math.max(1000,maxTime-minTime);
 const scaleX=t=>left+(Date.parse(t)-minTime)/range*(width-left-right);
 const scaleY=v=>top+(height-top-bottom)*(1-Math.max(0,Number(v))/max);
 const points=key=>selected.map(r=>
   scaleX(r.observed_at).toFixed(2)+','+scaleY(r[key]).toFixed(2)).join(' ');
 const label=v=>v>=1e9?(v/1e9).toFixed(1)+' Gbps':
  v>=1e6?(v/1e6).toFixed(1)+' Mbps':
  v>=1e3?(v/1e3).toFixed(1)+' kbps':v.toFixed(0)+' bps';
 return '<div class="tablewrap"><svg role="img" aria-label="SNMPv3 input and output throughput over time" viewBox="0 0 760 230" style="width:100%;height:auto;max-height:300px">'+
   '<line x1="'+left+'" y1="'+top+'" x2="'+left+'" y2="'+(height-bottom)+'" stroke="#8793a8"/>'+
   '<line x1="'+left+'" y1="'+(height-bottom)+'" x2="'+(width-right)+'" y2="'+(height-bottom)+'" stroke="#8793a8"/>'+
   '<polyline fill="none" stroke="#7160d9" stroke-width="2.5" points="'+points('input_bps')+'"/>'+
   '<polyline fill="none" stroke="#16a0a5" stroke-width="2.5" points="'+points('output_bps')+'"/>'+
   '<text x="8" y="'+(top+5)+'" font-size="11" fill="currentColor">'+esc(label(max))+'</text>'+
   '<text x="8" y="'+(height-bottom+4)+'" font-size="11" fill="currentColor">0</text>'+
   '<text x="'+left+'" y="'+(height-12)+'" font-size="11" fill="currentColor">'+esc(selected[0].observed_at.slice(0,16).replace('T',' '))+' UTC</text>'+
   '<text x="'+(width-190)+'" y="'+(height-12)+'" font-size="11" fill="currentColor">'+esc(selected[selected.length-1].observed_at.slice(0,16).replace('T',' '))+' UTC</text>'+
   '</svg><p class="muted"><span style="color:#7160d9">Input (into device)</span> · <span style="color:#16a0a5">Output (from device)</span>. Rates in bits/second, measured between polls.</p></div>';
}

function assignTcpSite(target,done,message){
 const sites=data.highsites||[];
 const d=dialog('<h2>Highsite and upstream relationship</h2><p class="note">Only tag a monitor as upstream when losing its TCP service reasonably indicates a shared upstream problem. This produces potential site incidents and suppresses downstream alert duplicates.</p><form id="topology-form"><label>Associated highsite</label><select name="highsiteId"><option value="">No site association</option>'+sites.map(x=>'<option value="'+esc(x.id)+'">'+esc(x.number)+' — '+esc(x.name)+'</option>').join('')+'</select><label><input type="checkbox" name="isUpstream" style="width:auto"> Shared upstream/critical backhaul monitor</label><p id="topology-error" class="error"></p><div class="buttons"><button class="primary">Save site relationship</button><button type="button" id="cancel">Cancel</button></div></form>');
 const form=d.querySelector('#topology-form'),site=form.querySelector('[name="highsiteId"]'),up=form.querySelector('[name="isUpstream"]');
 site.value=target.highsite_id||'';up.checked=Boolean(target.is_upstream);
 d.querySelector('#cancel').onclick=()=>d.close();
 form.onsubmit=async e=>{
  e.preventDefault();
  if(!confirm('Apply highsite correlation to '+target.name+'? Changing topology resets the monitor health state.'))return;
  try{
   await api('/monitor/targets/'+target.id+'/site','PUT',{
    highsiteId:site.value||null,isUpstream:up.checked,confirm:true
   });d.close();message.textContent='Site relationship saved.';await done();
  }catch(error){form.querySelector('#topology-error').textContent=error.message}
 };
}

function siteImpactConsole(el){
 el.innerHTML+='<h1>Highsite impact intelligence</h1><p class="sub">Correlated from explicitly tagged upstream TCP monitors. An upstream TCP failure indicates a potential outage, not confirmed loss of every site service.</p><div class="card"><div class="toolbar"><h2>Highsite exposure</h2><button id="site-impact-refresh">Refresh</button></div><div id="site-impact-summary">Loading…</div></div><div class="card topgap"><h2>Correlated outage and recovery history</h2><div id="site-impact-events">Loading…</div></div><p class="note">Active-customer and sector counts are the current inventory snapshot. The historical event table records counts at the time of the event. Correlation is based on the assigned highsite, not geographic proximity or inferred RF topology.</p>';
 const summary=el.querySelector('#site-impact-summary'),events=el.querySelector('#site-impact-events');
 async function draw(){
  try{
   const [sites,history]=await Promise.all([
    api('/monitor/sites'),api('/monitor/site-events')
   ]);
   const impacted=sites.filter(x=>Number(x.upstream_down)>0);
   summary.innerHTML='<div class="grid"><div class="card"><small>Potentially impacted highsites</small><div class="metric">'+impacted.length+'</div></div><div class="card"><small>Active customers at those sites</small><div class="metric">'+impacted.reduce((n,s)=>n+Number(s.affected_customers),0)+'</div></div><div class="card"><small>Sectors at those sites</small><div class="metric">'+impacted.reduce((n,s)=>n+Number(s.sectors),0)+'</div></div></div><div class="tablewrap"><table><thead><tr><th>Highsite</th><th>TCP monitors</th><th>Upstream</th><th>Upstream DOWN</th><th>Devices DOWN</th><th>Active customers</th><th>Sectors</th><th>Interpretation</th></tr></thead><tbody>'+sites.map(s=>'<tr><td>'+esc(s.name)+'</td><td>'+esc(s.monitored)+'</td><td>'+esc(s.upstream)+'</td><td>'+esc(s.upstream_down)+'</td><td>'+esc(s.devices_down)+'</td><td>'+esc(s.affected_customers)+'</td><td>'+esc(s.sectors)+'</td><td>'+esc(s.impact_state)+'</td></tr>').join('')+'</tbody></table></div>';
   events.innerHTML='<div class="tablewrap"><table><thead><tr><th>When</th><th>Highsite</th><th>Event</th><th>Potential customers</th><th>Sectors</th><th>Cause</th></tr></thead><tbody>'+history.map(e=>'<tr><td>'+esc(e.occurred_at)+'</td><td>'+esc(e.highsite)+'</td><td>'+esc(e.transition)+'</td><td>'+esc(e.impacted_customers)+'</td><td>'+esc(e.sectors)+'</td><td>'+esc(e.detail)+'</td></tr>').join('')+'</tbody></table>'+(history.length?'':'<p>No correlated incidents recorded yet.</p>')+'</div>';
  }catch(error){summary.textContent=error.message}
 }
 el.querySelector('#site-impact-refresh').onclick=draw;
 draw();
}

function snmpConsole(el){
 el.innerHTML+='<h1>SNMPv3 interface telemetry</h1><p class="sub">Encrypted authPriv credentials, IF-MIB 64-bit counters and historical throughput per interface. Read-only polling, no RouterOS configuration changes.</p><div class="card" id="snmp-add-card"><h2>Add SNMPv3 device</h2><form id="snmp-create"><div class="formgrid"><div><label>Source type</label><select id="snmp-source-type"><option value="equipment">Equipment</option><option value="nas">RADIUS NAS</option></select></div><div><label>Inventory device</label><select id="snmp-source" required></select></div><div><label>Device name</label><input name="name" required maxlength="160"></div><div><label>Highsite</label><select name="highsiteId"><option value="">Unassigned</option>'+(data.highsites||[]).map(s=>'<option value="'+esc(s.id)+'">'+esc(s.name)+'</option>').join('')+'</select></div><div><label>SNMPv3 USM username</label><input name="username" maxlength="80" required autocomplete="off"></div><div><label>Authentication algorithm</label><select name="authProtocol"><option value="SHA-256">SHA-256 (preferred if supported)</option><option value="SHA">SHA-1 compatibility</option></select></div><div><label>Authentication secret (12–128 supported characters)</label><input name="authPassword" type="password" autocomplete="new-password" required minlength="12"></div><div><label>AES privacy secret (12–128 characters)</label><input name="privacyPassword" type="password" autocomplete="new-password" required minlength="12"></div><div><label>UDP port</label><input name="udpPort" type="number" value="161" min="1" max="65535" required></div><div><label>Interface ifIndex</label><input name="interfaceIndex" type="number" value="1" min="1" required></div><div><label>Poll interval seconds</label><input name="intervalSeconds" type="number" value="300" min="60" max="3600" required></div></div><label><input name="enabled" type="checkbox" style="width:auto"> Enable SNMPv3 polling immediately</label><p class="note">SNMPv3 authPriv traffic uses UDP 161 to the exact saved device IP. Network credentials are encrypted with NetForge DataProtection keys. Back up the keys securely: losing them makes SNMP credentials unreadable.</p><div class="buttons"><button class="primary">Create SNMP profile</button></div><p class="error" id="snmp-create-error"></p></form></div><div class="card topgap"><div class="toolbar"><h2>Registered SNMPv3 interfaces</h2><button id="snmp-refresh">Refresh</button></div><div id="snmp-devices">Loading…</div></div><div class="card topgap"><h2>SNMPv3 state transitions</h2><div id="snmp-events">Loading…</div></div><div class="card topgap"><h2>Interface history</h2><div id="snmp-history">Select a registered device above to display throughput and operational state.</div></div>';
 const form=el.querySelector('#snmp-create'),sourceType=el.querySelector('#snmp-source-type'),source=el.querySelector('#snmp-source'),devices=el.querySelector('#snmp-devices'),events=el.querySelector('#snmp-events'),history=el.querySelector('#snmp-history'),error=el.querySelector('#snmp-create-error');
 function fillSources(){
  const collection=sourceType.value==='nas'?(data.nas||[]):(data.equipment||[]);
  source.innerHTML='<option value="">Select inventoried device</option>'+collection.map(x=>'<option value="'+esc(x.id)+'">'+esc(x.name||x.shortname)+' ('+esc(x.management_ip||x.nasname||'no IP')+')</option>').join('');
 }
 sourceType.onchange=fillSources;
 source.onchange=()=>{
  const rows=sourceType.value==='nas'?data.nas:data.equipment;
  const selected=(rows||[]).find(x=>String(x.id)===source.value);
  if(selected){
   form.querySelector('[name="name"]').value=selected.name||selected.shortname||'';
   if(selected.highsite_id)form.querySelector('[name="highsiteId"]').value=selected.highsite_id;
  }
 };
 if(role!=='admin')form.closest('.card').hidden=true;
 form.onsubmit=async e=>{
  e.preventDefault();error.textContent='';
  if(!source.value){error.textContent='Select a saved NAS or equipment asset.';return}
  const formValues=Object.fromEntries(new FormData(form));
  const enabled=form.querySelector('[name="enabled"]').checked;
  if(enabled&&!confirm('Enable SNMPv3 authPriv polling for '+formValues.name+'? UDP 161 packets will be sent from the API host.'))return;
  const payload={
   nasId:sourceType.value==='nas'?Number(source.value):null,
   equipmentId:sourceType.value==='equipment'?source.value:null,
   highsiteId:formValues.highsiteId||null,
   name:formValues.name,username:formValues.username,
   authProtocol:formValues.authProtocol,
   authPassword:formValues.authPassword,privacyPassword:formValues.privacyPassword,
   udpPort:Number(formValues.udpPort),interfaceIndex:Number(formValues.interfaceIndex),
   intervalSeconds:Number(formValues.intervalSeconds),enabled,confirm:enabled
  };
  try{
   await api('/snmp/devices','POST',payload);
   form.reset();fillSources();error.textContent='SNMPv3 profile saved. Secrets are never returned by the API.';
   await draw();
  }catch(err){error.textContent=err.message}
 };
 async function showHistory(id,name,mode='raw'){
  history.textContent='Loading samples for '+name+'…';
  try{
   const hourly=mode==='hourly';
   const records=await api(hourly?'/telemetry/interface-hourly/'+id+'?hours=2160':
    '/snmp/devices/'+id+'/history?hours=168');
   const plot=hourly?records.map(x=>({
    observed_at:x.hour_start,input_bps:x.input_average_bps,
    output_bps:x.output_average_bps
   })):records;
   const recent=records.slice(-80).reverse();
   history.innerHTML='<h3>'+esc(name)+' — '+(hourly?'90-day hourly history':'last 7 days raw samples')+'</h3>'+
    '<div class="buttons"><button id="snmp-raw">7-day samples</button><button id="snmp-hourly">90-day hourly average and peak</button></div>'+
    telemetryGraph(plot)+
    '<p class="muted">Interface input and output are device-relative. Counter resets exclude invalid bitrate intervals. Hourly rates show aggregate averages and peaks of valid recorded samples.</p>'+
    '<div class="tablewrap"><table><thead><tr><th>UTC sample</th>'+
    (hourly?'<th>Operational</th><th>Input avg Mbps</th><th>Input peak Mbps</th><th>Output avg Mbps</th><th>Output peak Mbps</th>':
    '<th>Interface state</th><th>Input Mbps</th><th>Output Mbps</th><th>Counter reset</th>')+
    '</tr></thead><tbody>'+
    recent.map(x=>hourly?'<tr><td>'+esc(x.hour_start)+'</td><td>'+esc(x.operational_samples)+' / '+esc(x.samples)+'</td><td>'+esc(x.input_average_bps===null?'—':(Number(x.input_average_bps)/1e6).toFixed(2))+'</td><td>'+esc(x.input_peak_bps===null?'—':(Number(x.input_peak_bps)/1e6).toFixed(2))+'</td><td>'+esc(x.output_average_bps===null?'—':(Number(x.output_average_bps)/1e6).toFixed(2))+'</td><td>'+esc(x.output_peak_bps===null?'—':(Number(x.output_peak_bps)/1e6).toFixed(2))+'</td></tr>':
    '<tr><td>'+esc(x.observed_at)+'</td><td>'+esc(x.oper_status===1?'UP':'not UP ('+x.oper_status+')')+'</td><td>'+esc(x.input_bps===null?'—':(Number(x.input_bps)/1e6).toFixed(2))+'</td><td>'+esc(x.output_bps===null?'—':(Number(x.output_bps)/1e6).toFixed(2))+'</td><td>'+esc(x.counter_reset?'Yes':'No')+'</td></tr>').join('')+'</tbody></table></div>';
   history.querySelector('#snmp-raw').onclick=()=>showHistory(id,name,'raw');
   history.querySelector('#snmp-hourly').onclick=()=>showHistory(id,name,'hourly');
  }catch(err){history.textContent=err.message}
 }
 async function rotateCredentials(record){
  const d=dialog('<h2>Rotate SNMPv3 authentication/privilege keys</h2><p class="note">New credentials must match the configuration on the device; changing them here does not change the device.</p><form id="snmp-rotate"><label>USM user</label><input name="username" value="'+esc(record.username)+'" required><label>Auth protocol</label><select name="authProtocol"><option value="SHA-256">SHA-256</option><option value="SHA">SHA-1</option></select><label>New authentication secret</label><input name="authPassword" type="password" minlength="12" required autocomplete="new-password"><label>New AES privacy secret</label><input name="privacyPassword" type="password" minlength="12" required autocomplete="new-password"><p id="rotate-error" class="error"></p><div class="buttons"><button class="primary">Rotate credentials</button><button type="button" id="close">Cancel</button></div></form>');
  d.querySelector('[name="authProtocol"]').value=record.auth_protocol;
  d.querySelector('#close').onclick=()=>d.close();
  d.querySelector('#snmp-rotate').onsubmit=async e=>{
   e.preventDefault();
   if(!confirm('Replace the SNMPv3 profile credentials? Existing ones cannot be recovered.'))return;
   try{await api('/snmp/devices/'+record.id+'/credentials','PUT',{...Object.fromEntries(new FormData(e.target)),confirm:true});d.close();await draw();}
   catch(err){d.querySelector('#rotate-error').textContent=err.message}
  };
 }
 async function draw(){
  try{
   const [all,alerts]=await Promise.all([api('/snmp/devices'),api('/snmp/events')]);
   devices.innerHTML='<div class="tablewrap"><table><thead><tr><th>Name</th><th>Host</th><th>Highsite</th><th>Interface</th><th>Auth</th><th>Polling</th><th>State</th><th>Failures</th><th>Last check</th><th>Actions</th></tr></thead><tbody>'+all.map(x=>'<tr><td>'+esc(x.name)+'</td><td>'+esc(x.address)+':'+esc(x.udp_port)+'</td><td>'+esc(x.highsite||'Unassigned')+'</td><td>'+esc(x.interface_index)+'</td><td>'+esc(x.auth_protocol)+'</td><td>'+esc(x.enabled?'Enabled':'Disabled')+'</td><td>'+esc(x.state)+'</td><td>'+esc(x.failure_streak)+'</td><td>'+esc(x.last_polled_at||'Never')+'</td><td><button data-history="'+esc(x.id)+'">History</button>'+(role==='admin'?' <button data-poll="'+esc(x.id)+'">Poll</button> <button data-toggle="'+esc(x.id)+'">'+(x.enabled?'Disable':'Enable')+'</button> <button data-rotate="'+esc(x.id)+'">Credentials</button>':'')+'</td></tr>').join('')+'</tbody></table></div>'+(all.length?'':'<p class="muted">No SNMPv3 profiles configured.</p>');
   devices.querySelectorAll('[data-history]').forEach(b=>b.onclick=()=>{const x=all.find(y=>y.id===b.dataset.history);if(x)showHistory(x.id,x.name)});
   devices.querySelectorAll('[data-poll]').forEach(b=>b.onclick=async()=>{
    const x=all.find(y=>y.id===b.dataset.poll);if(!x)return;
    try{const result=await api('/snmp/devices/'+x.id+'/poll','POST',{});error.textContent='Poll '+x.name+': '+result.state+(result.note?' / '+result.note:'');await draw();await showHistory(x.id,x.name)}
    catch(err){error.textContent=err.message}
   });
   devices.querySelectorAll('[data-toggle]').forEach(b=>b.onclick=async()=>{
    const x=all.find(y=>y.id===b.dataset.toggle);if(!x)return;
    const enabled=!x.enabled;
    if(enabled&&!confirm('Enable SNMPv3 polling at '+x.address+':'+x.udp_port+' for interface '+x.interface_index+'?'))return;
    try{await api('/snmp/devices/'+x.id,'PUT',{enabled,
     intervalSeconds:x.interval_seconds,interfaceIndex:x.interface_index,confirm:true});await draw()}
    catch(err){error.textContent=err.message}
   });
   devices.querySelectorAll('[data-rotate]').forEach(b=>b.onclick=()=>{
    const x=all.find(y=>y.id===b.dataset.rotate);if(x)rotateCredentials(x)
   });
   events.innerHTML='<div class="tablewrap"><table><thead><tr><th>When</th><th>Device</th><th>Transition</th><th>Reason</th><th>Suppressed by upstream</th></tr></thead><tbody>'+alerts.map(x=>'<tr><td>'+esc(x.occurred_at)+'</td><td>'+esc(x.name)+'</td><td>'+esc(x.transition)+'</td><td>'+esc(x.reason)+'</td><td>'+esc(x.suppressed?'Yes':'No')+'</td></tr>').join('')+'</tbody></table></div>';
  }catch(err){devices.textContent=err.message}
 }
 el.querySelector('#snmp-refresh').onclick=draw;
 (async()=>{try{
  if(role==='admin'){
   const [nas,equipment]=await Promise.all([api('/radius/nas-management'),api('/equipment')]);
   data.nas=nas;data.equipment=equipment;fillSources();
  }
  await draw();
 }catch(err){error.textContent=err.message}})();
}
