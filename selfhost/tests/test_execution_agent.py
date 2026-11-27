# root/selfhost/tests/test_execution_agent.py
"""Offline execution-agent tests. Never connect to a physical router."""
import datetime
import hashlib
import importlib.util
import pathlib
import sys
import unittest
import uuid
from unittest.mock import patch

MODULE=pathlib.Path(__file__).resolve().parents[1]/"execution-agent"/"netforge_execute.py"
SPEC=importlib.util.spec_from_file_location("netforge_execute",MODULE)
agent=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

class ExecutionAgentTests(unittest.TestCase):
    def setUp(self):
        self.command="/system identity print"
        self.hash=hashlib.sha256(self.command.encode()).hexdigest().upper()
        self.profile=str(uuid.uuid4())
        self.cfg={"profile_id":self.profile,"router_ip":"192.0.2.10",
                  "allowed_sha256":{self.hash}}
        self.job={"targetId":str(uuid.uuid4()),"claim":"A"*64,
                  "profileId":self.profile,"address":"192.0.2.10",
                  "command":self.command,"sha256":self.hash,
                  "deadline":(datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=120)).isoformat()}

    def test_rejects_unreviewed_target_and_multiline(self):
        self.assertTrue(agent.approved(self.cfg,self.job))
        for job in ({**self.job,"command":"/system reboot"},
                    {**self.job,"address":"192.0.2.11"},
                    {**self.job,"profileId":str(uuid.uuid4())},
                    {**self.job,"command":self.command+"\n"}):
            with patch.object(agent,"ssh_args") as ssh:
                self.assertEqual(agent.execute(self.cfg,job),("refused",0,None))
                ssh.assert_not_called()
        with patch.object(agent,"ssh_args") as ssh:
            expired={**self.job,"deadline":datetime.datetime.now(datetime.timezone.utc).isoformat()}
            self.assertEqual(agent.execute(self.cfg,expired),("refused",0,None))
            ssh.assert_not_called()

    def test_bounded_subprocess(self):
        # A local harmless Python process stands in for SSH; verify exit,
        # output counting, and termination when the output cap is reached.
        with patch.object(agent,"ssh_args",return_value=[sys.executable,"-c","print('test')"]):
            self.assertEqual(agent.execute(self.cfg,self.job),("exit_zero",5,0))
        with patch.object(agent,"ssh_args",return_value=[sys.executable,"-c","raise SystemExit(3)"]):
            self.assertEqual(agent.execute(self.cfg,self.job),("nonzero",0,3))
        with patch.object(agent,"ssh_args",return_value=[sys.executable,"-c","print('x'*70000)"]):
            self.assertEqual(agent.execute(self.cfg,self.job),("output_limit",65536,None))

    def test_report_failure_never_reexecutes(self):
        with patch.object(agent,"request",side_effect=[self.job,OSError("lost report")]) as request:
            with patch.object(agent,"execute",return_value=("exit_zero",5,0)) as execute:
                with self.assertRaises(OSError):
                    agent.run(self.cfg)
                execute.assert_called_once()
                self.assertEqual(request.call_count,2)
        with patch.object(agent,"request",return_value=None):
            with patch.object(agent,"execute") as execute:
                self.assertEqual(agent.run(self.cfg),0)
                execute.assert_not_called()

if __name__=="__main__":
    unittest.main()
